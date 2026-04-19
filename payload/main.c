/* PS5 Upload Server - Custom High-Speed Protocol
 * By Manos
 * Port: 9113
 * Protocol: Custom binary for maximum speed
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include <errno.h>
#include <stdint.h>
#include <sys/socket.h>
#include <netinet/in.h>
#include <netinet/tcp.h>
#include <arpa/inet.h>
#include <sys/stat.h>
#include <sys/statvfs.h>
#include <sys/mount.h>
#include <fcntl.h>
#include <dirent.h>
#include <pthread.h>
#include <time.h>
#include <ifaddrs.h>
#include <sys/wait.h>
#include <signal.h>
#include <poll.h>
#include <stdbool.h>
#include <ctype.h>
#include <stdarg.h>
#include <sys/sysctl.h>

// Sony PS5 kernel functions for hardware monitoring
// ONLY the APIs that appear in the official ps5-payload-sdk/samples/hwinfo/main.c
// Extra "hw info" APIs (power consumption, wlan/bt, optical, icc) were removed
// because they destabilize the payload process under repeated calls.
extern int  sceKernelGetHwModelName(char *name);
extern int  sceKernelGetHwSerialNumber(char *serial);
extern int  sceKernelGetCpuTemperature(int *temperature);
extern int  sceKernelGetSocSensorTemperature(int sensor_id, int *temperature);
extern long sceKernelGetCpuFrequency(void);
// PS5-specific memory query APIs (safe, just return sizes)
extern size_t sceKernelGetDirectMemorySize(void);
// Power consumption - called at most once every 5s to avoid destabilizing the payload
extern int sceKernelGetSocPowerConsumption(uint32_t *power_mw);

// Game launch APIs (both available in libSceSystemService)
// Low-level Lnc util - used by open-source launchers for mounted/sideloaded games
extern int  sceLncUtilInitialize(void);
extern int  sceLncUtilLaunchApp(const char *title_id, char *argv, void *param);
extern int  sceSystemServiceLaunchApp(const char *title_id, const char **argv, void *opts);
extern int  sceUserServiceInitialize(int *priority);
extern int  sceUserServiceGetForegroundUser(int *user_id);

// Sony PS5 app installation/registration
extern int sceAppInstUtilInitialize(void);
extern int sceAppInstUtilAppInstallTitleDir(const char *title_id, const char *base_path, void *reserved);
extern int sceAppInstUtilAppUnInstall(const char *title_id);

#include <sys/_iovec.h>
#include <sys/param.h>
#include <sys/uio.h>
#include <limits.h>

#define SERVER_PORT 9113
#define BUFFER_SIZE (8 * 1024 * 1024)  // 8MB for maximum throughput
#define MAX_PATH 2048
#define DISK_WORKER_COUNT 4
#define QUEUE_MAX_SIZE 32

// Game mounting defines
#define IOVEC_ENTRY(x) { (void*)(x), (x) ? strlen(x) + 1 : 0 }
#define IOVEC_SIZE(x)  (sizeof(x) / sizeof(struct iovec))
#define GAMES_BASE_PATH "/data/etaHEN/games"

// Per-file mutex hash map to prevent corruption during parallel uploads to SAME file
// Different files can write in parallel without blocking each other
typedef struct file_mutex_entry {
    char path[MAX_PATH];
    pthread_mutex_t mutex;
    int ref_count;
    struct file_mutex_entry *next;
} file_mutex_entry_t;

static file_mutex_entry_t *g_file_mutexes = NULL;
static pthread_mutex_t g_mutex_map_lock = PTHREAD_MUTEX_INITIALIZER;

// Get or create mutex for a specific file path
pthread_mutex_t* get_file_mutex(const char *path) {
    pthread_mutex_lock(&g_mutex_map_lock);
    
    // Search for existing mutex
    file_mutex_entry_t *entry = g_file_mutexes;
    while (entry) {
        if (strcmp(entry->path, path) == 0) {
            entry->ref_count++;
            pthread_mutex_unlock(&g_mutex_map_lock);
            return &entry->mutex;
        }
        entry = entry->next;
    }
    
    // Create new mutex for this file
    entry = (file_mutex_entry_t*)malloc(sizeof(file_mutex_entry_t));
    if (!entry) {
        pthread_mutex_unlock(&g_mutex_map_lock);
        return NULL;
    }
    
    strncpy(entry->path, path, sizeof(entry->path) - 1);
    entry->path[sizeof(entry->path) - 1] = '\0';
    pthread_mutex_init(&entry->mutex, NULL);
    entry->ref_count = 1;
    entry->next = g_file_mutexes;
    g_file_mutexes = entry;
    
    pthread_mutex_unlock(&g_mutex_map_lock);
    return &entry->mutex;
}

// Release mutex reference
void release_file_mutex(const char *path) {
    pthread_mutex_lock(&g_mutex_map_lock);
    
    file_mutex_entry_t **ptr = &g_file_mutexes;
    while (*ptr) {
        if (strcmp((*ptr)->path, path) == 0) {
            (*ptr)->ref_count--;
            if ((*ptr)->ref_count == 0) {
                file_mutex_entry_t *to_free = *ptr;
                *ptr = (*ptr)->next;
                pthread_mutex_destroy(&to_free->mutex);
                free(to_free);
            }
            break;
        }
        ptr = &(*ptr)->next;
    }
    
    pthread_mutex_unlock(&g_mutex_map_lock);
}

// Protocol commands
#define CMD_PING 0x01
#define CMD_LIST_STORAGE 0x02
#define CMD_LIST_DIR 0x03
#define CMD_CREATE_DIR 0x04
#define CMD_DELETE_FILE 0x05
#define CMD_DELETE_DIR 0x06
#define CMD_RENAME 0x07
#define CMD_COPY_FILE 0x08
#define CMD_MOVE_FILE 0x09
#define CMD_START_UPLOAD 0x10
#define CMD_UPLOAD_CHUNK 0x11
#define CMD_END_UPLOAD 0x12
#define CMD_DOWNLOAD_FILE 0x13
#define CMD_SHELL_OPEN 0x20
#define CMD_SHELL_EXEC 0x21
#define CMD_SHELL_INTERRUPT 0x22
#define CMD_SHELL_CLOSE 0x23
#define CMD_INDEX_START 0x40
#define CMD_INDEX_STATUS 0x41
#define CMD_SEARCH_INDEX 0x42
#define CMD_INDEX_CANCEL 0x43
#define CMD_MOUNT_GAMES 0x30
#define CMD_GET_FILE_INFO 0x31
#define CMD_GET_SYSTEM_INFO 0x32
#define CMD_VERIFY_FILE 0x33
#define CMD_GET_HW_INFO 0x34
#define CMD_GET_TEMPS 0x35
#define CMD_GET_RUNNING_APPS 0x36
#define CMD_KILL_APP 0x37
#define CMD_LAUNCH_BROWSER 0x38
#define CMD_GET_POWER_INFO 0x39
#define CMD_GET_GAME_LIST 0x3A
#define CMD_UNMOUNT_GAME 0x3B
#define CMD_GET_GAME_ICON 0x3C
#define CMD_GET_GAME_DETAILS 0x3D
#define CMD_GET_GAME_PIC 0x3E
#define CMD_LIST_SAVES 0x3F
#define CMD_LAUNCH_GAME 0x44
#define CMD_LIST_SCREENSHOTS 0x45
#define CMD_DELETE_SCREENSHOT 0x46
#define CMD_SHUTDOWN 0xFF

// Protocol responses
#define RESP_OK 0x01
#define RESP_ERROR 0x02
#define RESP_DATA 0x03
#define RESP_READY 0x04
#define RESP_PROGRESS 0x05

typedef struct notify_request {
    char useless1[45];
    char message[3075];
} notify_request_t;

int sceKernelSendNotificationRequest(int, notify_request_t*, size_t, int);

// Supported game paths - internal, USB drives, and M.2 SSD
static const char* GAME_SCAN_PATHS[] = {
    "/data/etaHEN/games",    // Internal etaHEN storage
    "/mnt/usb0/games",       // USB drive 0
    "/mnt/usb1/games",       // USB drive 1
    "/mnt/usb2/games",       // USB drive 2
    "/mnt/usb3/games",       // USB drive 3
    "/mnt/ext0/games",       // M.2 SSD
};
#define NUM_GAME_SCAN_PATHS (sizeof(GAME_SCAN_PATHS) / sizeof(GAME_SCAN_PATHS[0]))

// Duplicate detection - track mounted title IDs
static char g_mounted_ids[256][12];
static int g_mounted_count = 0;

static int is_duplicate_title(const char* title_id) {
    for (int i = 0; i < g_mounted_count; i++) {
        if (strcmp(g_mounted_ids[i], title_id) == 0)
            return 1;
    }
    return 0;
}

static void track_mounted_title(const char* title_id) {
    if (g_mounted_count < 256) {
        strncpy(g_mounted_ids[g_mounted_count], title_id, 11);
        g_mounted_ids[g_mounted_count][11] = '\0';
        g_mounted_count++;
    }
}

void send_notification(const char *msg) {
    notify_request_t req;
    memset(&req, 0, sizeof(req));
    strncpy(req.message, msg, sizeof(req.message) - 1);
    sceKernelSendNotificationRequest(0, &req, sizeof(req), 0);
}

// ============================================================================
// GAME MOUNTING FUNCTIONS
// ============================================================================

static int mount_nullfs(const char* src, const char* dst) {
    struct iovec iov[] = {
        IOVEC_ENTRY("fstype"), IOVEC_ENTRY("nullfs"),
        IOVEC_ENTRY("from"),   IOVEC_ENTRY(src),
        IOVEC_ENTRY("fspath"), IOVEC_ENTRY(dst),
    };
    return nmount(iov, IOVEC_SIZE(iov), 0);
}

static int is_mounted(const char* path) {
    struct statfs sfs;
    if (statfs(path, &sfs) != 0)
        return 0;
    return strcmp(sfs.f_fstypename, "nullfs") == 0;
}

// ---------------- COPY DIRECTORY ----------------
static int copy_dir(const char* src, const char* dst) {
    if (mkdir(dst, 0755) && errno != EEXIST) {
        return -1;
    }

    DIR* d = opendir(src);
    if (!d) return -1;

    struct dirent* e;
    char ss[PATH_MAX], dd[PATH_MAX];
    struct stat st;

    while ((e = readdir(d))) {
        if (!strcmp(e->d_name, ".") || !strcmp(e->d_name, "..")) continue;

        snprintf(ss, sizeof(ss), "%s/%s", src, e->d_name);
        snprintf(dd, sizeof(dd), "%s/%s", dst, e->d_name);

        if (stat(ss, &st) != 0) continue;

        if (S_ISDIR(st.st_mode)) {
            copy_dir(ss, dd);
        } else {
            unlink(dd);
            
            int src_fd = open(ss, O_RDONLY);
            if (src_fd < 0) continue;
            
            int dst_fd = open(dd, O_WRONLY | O_CREAT | O_TRUNC, 0644);
            if (dst_fd < 0) {
                close(src_fd);
                continue;
            }
            
            char* buf = (char*)malloc(2097152);
            if (buf) {
                ssize_t n;
                while ((n = read(src_fd, buf, 2097152)) > 0) {
                    write(dst_fd, buf, n);
                }
                free(buf);
            }
            
            close(src_fd);
            close(dst_fd);
        }
    }
    closedir(d);
    return 0;
}

// ---------------- COPY appmeta ----------------
static int is_appmeta_file(const char* name) {
    if (!strcasecmp(name, "param.json") ||
        !strcasecmp(name, "param.sfo"))
        return 1;

    const char* ext = strrchr(name, '.');
    if (!ext) return 0;

    return !strcasecmp(ext, ".png") ||
           !strcasecmp(ext, ".dds") ||
           !strcasecmp(ext, ".at9");
}

static int copy_sce_sys_to_appmeta(const char* src, const char* title_id) {
    char dst[PATH_MAX];
    snprintf(dst, sizeof(dst), "/user/appmeta/%s", title_id);

    mkdir("/user/appmeta", 0777);
    mkdir(dst, 0755);

    DIR* d = opendir(src);
    if (!d) return -1;

    struct dirent* e;
    char ss[PATH_MAX], dd[PATH_MAX];
    struct stat st;

    while ((e = readdir(d))) {
        if (!strcmp(e->d_name, ".") || !strcmp(e->d_name, ".."))
            continue;

        if (!is_appmeta_file(e->d_name))
            continue;

        snprintf(ss, sizeof(ss), "%s/%s", src, e->d_name);
        snprintf(dd, sizeof(dd), "%s/%s", dst, e->d_name);

        if (stat(ss, &st) != 0 || !S_ISREG(st.st_mode))
            continue;

        unlink(dd);
        
        int src_fd = open(ss, O_RDONLY);
        if (src_fd < 0) continue;
        
        int dst_fd = open(dd, O_WRONLY | O_CREAT | O_TRUNC, 0644);
        if (dst_fd < 0) {
            close(src_fd);
            continue;
        }
        
        char* buf = (char*)malloc(2097152);
        if (buf) {
            ssize_t n;
            while ((n = read(src_fd, buf, 2097152)) > 0) {
                write(dst_fd, buf, n);
            }
            free(buf);
        }
        
        close(src_fd);
        close(dst_fd);
    }

    closedir(d);
    return 0;
}

// ---------------- JSON HELPER ----------------
static int extract_json_string(const char* json, const char* key,
                               char* out, size_t out_size) {
    char search[64];
    snprintf(search, sizeof(search), "\"%s\"", key);

    const char* p = strstr(json, search);
    if (!p) return -1;

    p = strchr(p + strlen(search), ':');
    if (!p) return -1;

    while (*++p && isspace(*p));
    if (*p != '"') return -1;
    p++;

    size_t i = 0;
    while (i < out_size - 1 && p[i] && p[i] != '"') {
        out[i] = p[i];
        i++;
    }
    out[i] = '\0';
    return 0;
}

// ---------------- SFO READER FOR PS4 ----------------
typedef struct {
    uint16_t key_offset;
    uint16_t type;
    uint32_t size;
    uint32_t max_size;
    uint32_t data_offset;
} sfo_entry_t;

static int read_title_id_from_sfo(const char* path,
                                 char* title_id,
                                 size_t size)
{
    FILE* f = fopen(path, "rb");
    if (!f) return -1;

    uint32_t magic, version, key_off, data_off, count;
    if (fread(&magic, 4, 1, f) != 1 ||
        fread(&version, 4, 1, f) != 1 ||
        fread(&key_off, 4, 1, f) != 1 ||
        fread(&data_off, 4, 1, f) != 1 ||
        fread(&count, 4, 1, f) != 1) {
        fclose(f);
        return -1;
    }

    if (magic != 0x46535000) {
        fclose(f);
        return -1;
    }

    for (uint32_t i = 0; i < count; i++) {
        sfo_entry_t entry;
        if (fseek(f, 0x14 + i * sizeof(sfo_entry_t), SEEK_SET) != 0) continue;
        if (fread(&entry, sizeof(sfo_entry_t), 1, f) != 1) continue;

        char key[128] = {};
        if (fseek(f, key_off + entry.key_offset, SEEK_SET) != 0) continue;
        if (fread(key, 1, sizeof(key) - 1, f) <= 0) continue;

        for (int k = 0; k < sizeof(key); k++) {
            if (key[k] == '\0' || !isprint(key[k])) {
                key[k] = '\0';
                break;
            }
        }

        if (strncmp(key, "TITLE_ID", 8) == 0) {
            if (fseek(f, data_off + entry.data_offset, SEEK_SET) != 0) continue;
            size_t rlen = (entry.size < size - 1) ? entry.size : size - 1;
            if (fread(title_id, 1, rlen, f) <= 0) continue;
            title_id[rlen] = '\0';

            for (int j = rlen - 1; j >= 0; j--) {
                if (title_id[j] == '\0' || isspace(title_id[j]))
                    title_id[j] = '\0';
                else
                    break;
            }

            fclose(f);
            return 0;
        }
    }

    fclose(f);
    return -1;
}

// ---------------- GET GAME REGION ----------------
static const char* get_game_region(const char* title_id) {
    if (!title_id || strlen(title_id) < 4) return "Unknown";
    
    if (strncmp(title_id, "PPSA", 4) == 0) {
        char region_code = title_id[4];
        switch (region_code) {
            case '0': return "US";
            case '1': return "EU";
            case '2': return "JP";
            case '3': return "Asia";
            case '4': return "UK";
            case '5': return "KR";
            default: return "World";
        }
    }
    
    if (strncmp(title_id, "CUSA", 4) == 0) {
        char region_code = title_id[4];
        switch (region_code) {
            case '0': return "US";
            case '1': return "EU";
            case '2': return "JP";
            case '3': return "Asia";
            case '4': return "UK";
            case '5': return "KR";
            default: return "World";
        }
    }
    
    if (strncmp(title_id, "NPXS", 4) == 0) return "System";
    if (strncmp(title_id, "NPWR", 4) == 0) return "World";
    
    return "Unknown";
}

// ---------------- GET GAME NAME ----------------
static int get_game_name_from_json(const char* json_path, char* name, size_t size) {
    FILE* f = fopen(json_path, "rb");
    if (!f) return -1;

    fseek(f, 0, SEEK_END);
    long len = ftell(f);
    fseek(f, 0, SEEK_SET);

    if (len <= 0 || len > 1024 * 1024) {
        fclose(f);
        return -1;
    }

    char* buf = (char*)malloc(len + 1);
    if (!buf) { fclose(f); return -1; }

    fread(buf, 1, len, f);
    buf[len] = '\0';
    fclose(f);

    if (extract_json_string(buf, "contentName", name, size) == 0) {
        name[strcspn(name, "\r\n")] = '\0';
        free(buf);
        return 0;
    }
    
    if (extract_json_string(buf, "titleName", name, size) == 0) {
        name[strcspn(name, "\r\n")] = '\0';
        free(buf);
        return 0;
    }
    
    const char* title_search = strstr(buf, "\"titleName\"");
    if (title_search) {
        const char* colon = strchr(title_search, ':');
        if (colon) {
            colon++;
            while (*colon == ' ' || *colon == '\t' || *colon == '\n' || *colon == '\r') colon++;
            if (*colon == '\"') {
                colon++;
                const char* end_quote = strchr(colon, '\"');
                if (end_quote && (end_quote - colon) < (long)size) {
                    memcpy(name, colon, end_quote - colon);
                    name[end_quote - colon] = '\0';
                    free(buf);
                    return 0;
                }
            }
        }
    }

    free(buf);
    return -1;
}

// ---------------- GET TITLE_ID ----------------
static int get_title_id_from_dir(const char* game_dir, char* title_id, size_t size) {
    char path[PATH_MAX];

    snprintf(path, sizeof(path), "%s/sce_sys/param.json", game_dir);
    FILE* f = fopen(path, "rb");
    if (f) {
        fseek(f, 0, SEEK_END);
        long len = ftell(f);
        fseek(f, 0, SEEK_SET);

        if (len > 0 && len < 1024 * 1024) {
            char* buf = (char*)malloc(len + 1);
            if (buf) {
                fread(buf, 1, len, f);
                buf[len] = '\0';

                if (extract_json_string(buf, "titleId", title_id, size) == 0 ||
                    extract_json_string(buf, "title_id", title_id, size) == 0) {
                    title_id[strcspn(title_id, "\r\n")] = '\0';
                    free(buf);
                    fclose(f);
                    return 0;
                }
                free(buf);
            }
        }
        fclose(f);
    }

    snprintf(path, sizeof(path), "%s/sce_sys/param.sfo", game_dir);
    return read_title_id_from_sfo(path, title_id, size);
}

// ---------------- PATCH DRM (PS5 only) ----------------
static int fix_application_drm_type(const char* path) {
    FILE* f = fopen(path, "rb");
    if (!f) return -1;

    fseek(f, 0, SEEK_END);
    long len = ftell(f);
    fseek(f, 0, SEEK_SET);

    if (len <= 0 || len > 1024 * 1024) {
        fclose(f);
        return -1;
    }

    char* buf = (char*)malloc(len + 1);
    if (!buf) { fclose(f); return -1; }

    fread(buf, 1, len, f);
    buf[len] = '\0';
    fclose(f);

    const char* key = "\"applicationDrmType\"";
    char* p = strstr(buf, key);
    if (!p) { free(buf); return 0; }

    char* colon = strchr(p + strlen(key), ':');
    char* q1 = colon ? strchr(colon, '"') : NULL;
    char* q2 = q1 ? strchr(q1 + 1, '"') : NULL;
    if (!q1 || !q2) { free(buf); return -1; }

    if ((q2 - q1 - 1) == strlen("standard") &&
        !strncmp(q1 + 1, "standard", strlen("standard"))) {
        free(buf);
        return 0;
    }

    size_t new_len = (q1 - buf) + 1 + strlen("standard") + 1 + strlen(q2 + 1);
    char* out = (char*)malloc(new_len + 1);
    if (!out) { free(buf); return -1; }

    memcpy(out, buf, q1 - buf + 1);
    memcpy(out + (q1 - buf + 1), "standard", strlen("standard"));
    strcpy(out + (q1 - buf + 1 + strlen("standard")), q2);

    f = fopen(path, "wb");
    if (!f) { free(buf); free(out); return -1; }

    fwrite(out, 1, strlen(out), f);
    fclose(f);

    free(buf);
    free(out);
    return 1;
}

// ---------------- CHECK IF ALREADY MOUNTED ----------------
static int is_game_already_mounted(const char* title_id, const char* game_path) {
    char mount_lnk_path[PATH_MAX];
    char system_ex_app[PATH_MAX];
    
    snprintf(mount_lnk_path, sizeof(mount_lnk_path), 
             "/user/app/%s/mount.lnk", title_id);
    
    FILE* f = fopen(mount_lnk_path, "r");
    if (f) {
        char existing_path[PATH_MAX] = {};
        if (fgets(existing_path, sizeof(existing_path), f)) {
            existing_path[strcspn(existing_path, "\r\n")] = '\0';
            fclose(f);
            
            if (strcmp(existing_path, game_path) == 0) {
                snprintf(system_ex_app, sizeof(system_ex_app),
                         "/system_ex/app/%s", title_id);
                if (is_mounted(system_ex_app)) {
                    return 1;
                }
            }
        } else {
            fclose(f);
        }
    }
    
    return 0;
}

// ---------------- PROCESS ONE GAME ----------------
static int process_game(const char* game_path, char* game_name_out, size_t name_size, char* title_id_out, size_t tid_size) {
    char title_id[12] = {};
    char game_name[256] = "Unknown Game";
    char system_ex_app[PATH_MAX];
    char user_app_dir[PATH_MAX];
    char src_sce_sys[PATH_MAX];
    char mount_lnk_path[PATH_MAX];
    char param_json_path[PATH_MAX];

    // Check sce_sys directory exists
    char sce_sys_check[PATH_MAX];
    snprintf(sce_sys_check, sizeof(sce_sys_check), "%s/sce_sys", game_path);
    struct stat sce_st;
    if (stat(sce_sys_check, &sce_st) != 0 || !S_ISDIR(sce_st.st_mode)) {
        return -1;
    }

    if (get_title_id_from_dir(game_path, title_id, sizeof(title_id))) {
        return -1;
    }

    // Duplicate detection
    if (is_duplicate_title(title_id)) {
        return 3;
    }

    // Copy title_id out if requested
    if (title_id_out && tid_size > 0) {
        strncpy(title_id_out, title_id, tid_size - 1);
        title_id_out[tid_size - 1] = '\0';
    }

    snprintf(param_json_path, sizeof(param_json_path),
             "%s/sce_sys/param.json", game_path);
    
    if (get_game_name_from_json(param_json_path, game_name, sizeof(game_name)) != 0) {
        snprintf(game_name, sizeof(game_name), "%s", title_id);
    }
    
    const char* region = get_game_region(title_id);
    
    if (game_name_out && name_size > 0) {
        snprintf(game_name_out, name_size, "%s [%s]", game_name, region);
    }
    
    if (is_game_already_mounted(title_id, game_path)) {
        track_mounted_title(title_id);
        return 2;
    }

    fix_application_drm_type(param_json_path);

    snprintf(system_ex_app, sizeof(system_ex_app),
             "/system_ex/app/%s", title_id);

    mkdir(system_ex_app, 0755);

    if (is_mounted(system_ex_app)) {
        unmount(system_ex_app, 0);
    }

    if (mount_nullfs(game_path, system_ex_app)) {
        return -1;
    }

    snprintf(user_app_dir, sizeof(user_app_dir),
             "/user/app/%s", title_id);
    char user_sce_sys[PATH_MAX];
    snprintf(user_sce_sys, sizeof(user_sce_sys),
             "%s/sce_sys", user_app_dir);

    mkdir(user_app_dir, 0755);
    mkdir(user_sce_sys, 0755);

    snprintf(src_sce_sys, sizeof(src_sce_sys),
             "%s/sce_sys", game_path);

    copy_dir(src_sce_sys, user_sce_sys);
    copy_sce_sys_to_appmeta(src_sce_sys, title_id);

    // Register the game with the PS5 system so it appears on the home screen
    // Args: title_id, base_path ("/user/app/"), flags (0)
    if (sceAppInstUtilAppInstallTitleDir(title_id, "/user/app/", 0)) {
        return -1;
    }

    // Write mount.lnk AFTER registration to track the source path
    snprintf(mount_lnk_path, sizeof(mount_lnk_path), 
             "/user/app/%s/mount.lnk", title_id);

    FILE* lnk_file = fopen(mount_lnk_path, "w");
    if (lnk_file) {
        fprintf(lnk_file, "%s", game_path);
        fclose(lnk_file);
    }

    track_mounted_title(title_id);
    return 0;
}

// Forward declaration for rmdir_recursive (defined later in file)
int rmdir_recursive(const char *path);

// ---------------- AUTO UNMOUNT DELETED GAMES ----------------
static int auto_unmount_deleted_games(void) {
    DIR* d = opendir("/user/app");
    if (!d) return 0;

    int unmounted = 0;
    struct dirent* e;

    while ((e = readdir(d))) {
        if (!strcmp(e->d_name, ".") || !strcmp(e->d_name, ".."))
            continue;

        if ((strncmp(e->d_name, "CUSA", 4) != 0 && 
             strncmp(e->d_name, "PPSA", 4) != 0) || 
            strlen(e->d_name) != 9)
            continue;

        char mount_lnk[PATH_MAX];
        snprintf(mount_lnk, sizeof(mount_lnk), "/user/app/%s/mount.lnk", e->d_name);

        FILE* f = fopen(mount_lnk, "r");
        if (!f) continue;

        char game_path[PATH_MAX] = {};
        if (fgets(game_path, sizeof(game_path), f)) {
            game_path[strcspn(game_path, "\r\n")] = '\0';
            fclose(f);

            struct stat st;
            if (stat(game_path, &st) != 0 || !S_ISDIR(st.st_mode)) {
                char system_ex_app[PATH_MAX];
                snprintf(system_ex_app, sizeof(system_ex_app), 
                         "/system_ex/app/%s", e->d_name);
                
                if (is_mounted(system_ex_app)) {
                    unmount(system_ex_app, 0);
                }
                
                char user_app_dir[PATH_MAX];
                snprintf(user_app_dir, sizeof(user_app_dir), 
                         "/user/app/%s", e->d_name);
                rmdir_recursive(user_app_dir);
                
                char appmeta_dir[PATH_MAX];
                snprintf(appmeta_dir, sizeof(appmeta_dir), 
                         "/user/appmeta/%s", e->d_name);
                rmdir_recursive(appmeta_dir);
                
                unmounted++;
            }
        } else {
            fclose(f);
        }
    }

    closedir(d);
    return unmounted;
}

// ============================================================================
// GAME MOUNTING FUNCTIONS - END
// ============================================================================

typedef struct {
    int sock;
    int upload_fd;  // File descriptor for direct write (faster than FILE*)
    pthread_mutex_t *file_mutex;  // Per-file mutex
    char upload_path[MAX_PATH];
    uint64_t upload_size;
    uint64_t upload_received;
    uint64_t current_offset;  // Current write offset for pwrite() - set by START_UPLOAD
    // Shell session
    FILE *shell_pipe;
    pid_t shell_pid;
    bool shell_active;
    char shell_cwd[MAX_PATH];
} client_session_t;

// Filesystem index entry (in-memory, no SQLite for simplicity)
typedef struct index_entry {
    char path[MAX_PATH];
    char name[256];
    uint64_t size;
    time_t mtime;
    bool is_dir;
    struct index_entry *next;
} index_entry_t;

// Index state
typedef struct {
    index_entry_t *entries;
    int total_files;
    int total_dirs;
    bool indexing;
    bool ready;
    pthread_mutex_t mutex;
    pthread_t thread;
} index_state_t;

static index_state_t g_index = {0};

// Disk write job for queue
typedef struct write_job {
    uint8_t *data;
    size_t len;
    FILE *fp;
    struct write_job *next;
} write_job_t;

// Job queue (producer-consumer pattern)
typedef struct {
    write_job_t *head;
    write_job_t *tail;
    size_t count;
    size_t max;
    int closed;
    pthread_mutex_t mutex;
    pthread_cond_t not_empty;
    pthread_cond_t not_full;
} job_queue_t;

// Global queue and workers
static job_queue_t g_queue;
static pthread_t g_workers[DISK_WORKER_COUNT];
static int g_workers_initialized = 0;

// Queue operations
void queue_init(job_queue_t *q, size_t max) {
    memset(q, 0, sizeof(*q));
    q->max = max;
    pthread_mutex_init(&q->mutex, NULL);
    pthread_cond_init(&q->not_empty, NULL);
    pthread_cond_init(&q->not_full, NULL);
}

int queue_push(job_queue_t *q, write_job_t *job) {
    pthread_mutex_lock(&q->mutex);
    while (!q->closed && q->count >= q->max) {
        pthread_cond_wait(&q->not_full, &q->mutex);
    }
    if (q->closed) {
        pthread_mutex_unlock(&q->mutex);
        return -1;
    }
    job->next = NULL;
    if (!q->tail) {
        q->head = job;
        q->tail = job;
    } else {
        q->tail->next = job;
        q->tail = job;
    }
    q->count++;
    pthread_cond_signal(&q->not_empty);
    pthread_mutex_unlock(&q->mutex);
    return 0;
}

write_job_t *queue_pop(job_queue_t *q) {
    pthread_mutex_lock(&q->mutex);
    while (!q->closed && q->count == 0) {
        pthread_cond_wait(&q->not_empty, &q->mutex);
    }
    if (q->count == 0 && q->closed) {
        pthread_mutex_unlock(&q->mutex);
        return NULL;
    }
    write_job_t *job = q->head;
    q->head = job->next;
    if (!q->head) {
        q->tail = NULL;
    }
    q->count--;
    pthread_cond_signal(&q->not_full);
    pthread_mutex_unlock(&q->mutex);
    return job;
}

// Disk worker thread
void *disk_worker(void *arg) {
    (void)arg;
    while (1) {
        write_job_t *job = queue_pop(&g_queue);
        if (!job) break;
        
        if (job->fp && job->data && job->len > 0) {
            fwrite(job->data, 1, job->len, job->fp);
            // No fflush here - let setvbuf handle buffering
        }
        
        free(job->data);
        free(job);
    }
    return NULL;
}

void init_workers() {
    if (g_workers_initialized) return;
    
    queue_init(&g_queue, QUEUE_MAX_SIZE);
    for (int i = 0; i < DISK_WORKER_COUNT; i++) {
        pthread_create(&g_workers[i], NULL, disk_worker, NULL);
    }
    g_workers_initialized = 1;
}

// Reliable send - loops until all bytes are sent or error
static ssize_t send_all(int sock, const void *buf, size_t len) {
    const uint8_t *ptr = (const uint8_t *)buf;
    size_t sent = 0;
    while (sent < len) {
        ssize_t n = send(sock, ptr + sent, len - sent, 0);
        if (n <= 0) return -1;
        sent += n;
    }
    return (ssize_t)sent;
}

// Send response - combined header+data in single send for speed
void send_response(int sock, uint8_t response, const void *data, uint32_t data_len) {
    // Combine header and data into single buffer for single send()
    size_t total_len = 5 + data_len;
    uint8_t *combined = malloc(total_len);
    if (!combined) {
        // Fallback to separate sends
        uint8_t header[5];
        header[0] = response;
        memcpy(header + 1, &data_len, 4);
        send_all(sock, header, 5);
        if (data && data_len > 0) {
            send_all(sock, data, data_len);
        }
        return;
    }
    
    combined[0] = response;
    memcpy(combined + 1, &data_len, 4);
    if (data && data_len > 0) {
        memcpy(combined + 5, data, data_len);
    }
    
    send_all(sock, combined, total_len);
    free(combined);
}

// Send OK response
void send_ok(int sock, const char *msg) {
    uint32_t len = msg ? strlen(msg) : 0;
    send_response(sock, RESP_OK, msg, len);
}

// Send error response
void send_error(int sock, const char *msg) {
    uint32_t len = msg ? strlen(msg) : 0;
    send_response(sock, RESP_ERROR, msg, len);
}

// Normalize path by removing double slashes
void normalize_path(char *path) {
    char *src = path;
    char *dst = path;
    int prev_slash = 0;
    
    while (*src) {
        if (*src == '/') {
            if (!prev_slash) {
                *dst++ = *src;
            }
            prev_slash = 1;
        } else {
            *dst++ = *src;
            prev_slash = 0;
        }
        src++;
    }
    *dst = '\0';
}

// Recursive directory creation
int mkdir_recursive(const char *path) {
    char tmp[MAX_PATH];
    char *p = NULL;
    size_t len;

    snprintf(tmp, sizeof(tmp), "%s", path);
    normalize_path(tmp);  // Remove any double slashes
    len = strlen(tmp);
    if (tmp[len - 1] == '/') {
        tmp[len - 1] = 0;
    }

    for (p = tmp + 1; *p; p++) {
        if (*p == '/') {
            *p = 0;
            if (mkdir(tmp, 0777) != 0 && errno != EEXIST) {
                return -1;
            }
            chmod(tmp, 0777);
            *p = '/';
        }
    }
    if (mkdir(tmp, 0777) != 0 && errno != EEXIST) {
        return -1;
    }
    chmod(tmp, 0777);
    return 0;
}

// Global deletion progress counter (protected by g_progress_mutex)
static int g_delete_count = 0;
static int g_total_files = 0;
static time_t g_last_notify = 0;
static int g_client_sock = 0;
static pthread_mutex_t g_progress_mutex = PTHREAD_MUTEX_INITIALIZER;

// Forward declaration
void send_progress_message(const char *msg);

// Global scan counter for progress updates (reset before each scan)
static int g_scan_count = 0;
static time_t g_last_scan_notify = 0;

// Count files in directory recursively with progress updates
int count_files_recursive(const char *path) {
    DIR *dir = opendir(path);
    if (!dir) {
        return 0;
    }

    int count = 0;
    struct dirent *entry;
    char child[MAX_PATH];
    
    while ((entry = readdir(dir)) != NULL) {
        if (strcmp(entry->d_name, ".") == 0 || strcmp(entry->d_name, "..") == 0) {
            continue;
        }
        snprintf(child, sizeof(child), "%s/%s", path, entry->d_name);

        struct stat st;
        if (stat(child, &st) != 0) {
            continue;
        }
        if (S_ISDIR(st.st_mode)) {
            count++;  // Count the directory itself
            count += count_files_recursive(child);  // Count its contents
        } else {
            count++;
            g_scan_count++;
            
            // Send progress every 500 files or every 3 seconds during counting
            time_t now = time(NULL);
            if (g_scan_count % 500 == 0 || (now - g_last_scan_notify) >= 3) {
                char msg[256];
                snprintf(msg, sizeof(msg), "📊 Scanning... found %d files so far", g_scan_count);
                send_progress_message(msg);
                g_last_scan_notify = now;
            }
        }
    }
    closedir(dir);
    return count;
}

// Send progress message to client (thread-safe)
void send_progress_message(const char *msg) {
    pthread_mutex_lock(&g_progress_mutex);
    if (g_client_sock > 0) {
        uint8_t header[5];
        header[0] = RESP_PROGRESS;
        uint32_t len = strlen(msg) + 1;
        memcpy(header + 1, &len, 4);
        send_all(g_client_sock, header, 5);
        send_all(g_client_sock, msg, len);
    }
    pthread_mutex_unlock(&g_progress_mutex);
}

// Recursive directory deletion with progress reporting
int rmdir_recursive(const char *path) {
    DIR *dir = opendir(path);
    if (!dir) {
        return -1;
    }

    struct dirent *entry;
    char child[MAX_PATH];
    while ((entry = readdir(dir)) != NULL) {
        if (strcmp(entry->d_name, ".") == 0 || strcmp(entry->d_name, "..") == 0) {
            continue;
        }
        snprintf(child, sizeof(child), "%s/%s", path, entry->d_name);

        struct stat st;
        if (stat(child, &st) != 0) {
            continue;
        }
        if (S_ISDIR(st.st_mode)) {
            rmdir_recursive(child);
        } else {
            unlink(child);
            g_delete_count++;
            
            // Send progress every 50 files or every 2 seconds
            time_t now = time(NULL);
            if (g_delete_count % 50 == 0 || (now - g_last_notify) >= 2) {
                int percentage = (g_total_files > 0) ? (g_delete_count * 100 / g_total_files) : 0;
                char msg[256];
                snprintf(msg, sizeof(msg), "🗑️ Deleting... %d/%d files (%d%%)", 
                         g_delete_count, g_total_files, percentage);
                send_progress_message(msg);
                g_last_notify = now;
            }
        }
    }
    closedir(dir);
    return rmdir(path);
}

// Handle PING
void handle_ping(client_session_t *session) {
    send_ok(session->sock, "PONG");
}

void handle_list_storage(client_session_t *session) {
    struct statfs sf;
    
    // PS5 Settings "Console Storage" formula:
    //   Total = (/user total - /user reserved) + /system_data total + /system_ex total
    //   Free  = /user bavail + /system_data bavail + /system_ex bavail
    //
    // Real PS5 example:
    //   /user      total=872.69 GiB, reserved=92.53 GiB, bavail=346.93 GiB
    //   /system_data total=7.98 GiB, bavail=6.83 GiB
    //   /system_ex   total=1.50 GiB, bavail=0.15 GiB
    //   → Total = 780.16 + 7.98 + 1.50 = 789.64 GiB = 847.9 GB (PS5 shows 848 GB) ✓
    
    if (statfs("/user", &sf) != 0) {
        send_error(session->sock, "statfs /user failed");
        return;
    }
    uint64_t u_blksz  = sf.f_bsize;
    uint64_t u_total  = (uint64_t)sf.f_blocks * u_blksz;
    uint64_t u_bfree  = (uint64_t)sf.f_bfree  * u_blksz;
    uint64_t u_bavail = (sf.f_bavail > 0) ? (uint64_t)sf.f_bavail * u_blksz : u_bfree;
    uint64_t u_rsvd   = (u_bfree > u_bavail) ? (u_bfree - u_bavail) : 0;
    uint64_t u_displayable = u_total - u_rsvd;
    
    uint64_t sd_total = 0, sd_bavail = 0;
    if (statfs("/system_data", &sf) == 0) {
        uint64_t sd_blksz = sf.f_bsize;
        sd_total  = (uint64_t)sf.f_blocks * sd_blksz;
        uint64_t sd_bfree = (uint64_t)sf.f_bfree * sd_blksz;
        sd_bavail = (sf.f_bavail > 0) ? (uint64_t)sf.f_bavail * sd_blksz : sd_bfree;
    }
    
    uint64_t sx_total = 0, sx_bavail = 0;
    if (statfs("/system_ex", &sf) == 0) {
        uint64_t sx_blksz = sf.f_bsize;
        sx_total  = (uint64_t)sf.f_blocks * sx_blksz;
        uint64_t sx_bfree = (uint64_t)sf.f_bfree * sx_blksz;
        sx_bavail = (sf.f_bavail > 0) ? (uint64_t)sf.f_bavail * sx_blksz : sx_bfree;
    }
    
    uint64_t total_bytes = u_displayable + sd_total + sx_total;
    uint64_t free_bytes  = u_bavail + sd_bavail + sx_bavail;
    uint64_t used_bytes  = (total_bytes > free_bytes) ? (total_bytes - free_bytes) : 0;
    uint64_t reserved_bytes = u_rsvd;
    
    uint64_t mounted_games_size = (used_bytes * 95) / 100;
    uint64_t user_data_size     = used_bytes - mounted_games_size;
    
    char response[512];
    snprintf(response, sizeof(response),
             "%llu|%llu|%llu|%llu|%llu|%llu|%llu|%s",
             (unsigned long long)total_bytes,
             (unsigned long long)free_bytes,
             (unsigned long long)free_bytes,
             (unsigned long long)reserved_bytes,
             (unsigned long long)mounted_games_size,
             (unsigned long long)user_data_size,
             (unsigned long long)free_bytes,
             "Console Storage");
    
    send_response(session->sock, RESP_DATA, response, strlen(response));
}

// Handle LIST_DIR - Optimized version using d_type only (no stat for dirs)
void handle_list_dir(client_session_t *session, const char *path) {
    char norm_path[MAX_PATH];
    snprintf(norm_path, sizeof(norm_path), "%s", path);
    normalize_path(norm_path);
    
    DIR *dir = opendir(norm_path);
    if (!dir) {
        int32_t count = 0;
        send_response(session->sock, RESP_DATA, &count, 4);
        return;
    }
    
    size_t buf_size = 256 * 1024;
    uint8_t *buffer = malloc(buf_size);
    if (!buffer) {
        closedir(dir);
        int32_t count = 0;
        send_response(session->sock, RESP_DATA, &count, 4);
        return;
    }
    
    uint8_t *ptr = buffer + 4;
    int32_t entry_count = 0;
    
    struct dirent *entry;
    char full_path[MAX_PATH];
    
    while ((entry = readdir(dir)) != NULL) {
        if (strcmp(entry->d_name, ".") == 0 || strcmp(entry->d_name, "..") == 0) {
            continue;
        }
        
        uint16_t name_len = (uint16_t)strlen(entry->d_name);
        size_t needed = 1 + 2 + name_len + 8 + 8;
        
        if ((size_t)(ptr - buffer) + needed > buf_size) {
            break;
        }
        
        // Determine type and get file size
        uint8_t type = 0;
        uint64_t size = 0;
        uint64_t timestamp = 0;
        
        snprintf(full_path, sizeof(full_path), "%s/%s", norm_path, entry->d_name);
        struct stat st;
        
        if (entry->d_type == DT_DIR) {
            type = 1;
        } else if (entry->d_type == DT_UNKNOWN) {
            // d_type not supported on this filesystem - use stat() as fallback
            if (stat(full_path, &st) == 0) {
                if (S_ISDIR(st.st_mode)) {
                    type = 1;
                } else {
                    size = st.st_size;
                    timestamp = st.st_mtime;
                }
            }
        } else {
            // Regular file - get size
            if (stat(full_path, &st) == 0) {
                size = st.st_size;
                timestamp = st.st_mtime;
            }
        }
        
        *ptr++ = type;
        memcpy(ptr, &name_len, 2);
        ptr += 2;
        memcpy(ptr, entry->d_name, name_len);
        ptr += name_len;
        memcpy(ptr, &size, 8);
        ptr += 8;
        memcpy(ptr, &timestamp, 8);
        ptr += 8;
        
        entry_count++;
    }
    
    closedir(dir);
    memcpy(buffer, &entry_count, 4);
    send_response(session->sock, RESP_DATA, buffer, (uint32_t)(ptr - buffer));
    free(buffer);
}

// Handle CREATE_DIR
void handle_create_dir(client_session_t *session, const char *path) {
    if (mkdir_recursive(path) == 0) {
        send_ok(session->sock, "Directory created");
    } else {
        send_error(session->sock, "Failed to create directory");
    }
}

// Handle DELETE_FILE
void handle_delete_file(client_session_t *session, const char *path) {
    char normalized_path[MAX_PATH];
    snprintf(normalized_path, sizeof(normalized_path), "%s", path);
    normalize_path(normalized_path);
    
    if (unlink(normalized_path) == 0) {
        send_ok(session->sock, "File deleted");
    } else {
        send_error(session->sock, "Failed to delete file");
    }
}

// Background deletion thread data
typedef struct {
    char path[MAX_PATH];
    int client_sock;
} delete_thread_data_t;

// Background deletion thread
void* delete_thread_func(void* arg) {
    delete_thread_data_t* data = (delete_thread_data_t*)arg;
    
    // Reset ALL progress counters
    g_delete_count = 0;
    g_scan_count = 0;
    g_last_notify = time(NULL);
    g_last_scan_notify = time(NULL);
    g_client_sock = data->client_sock;
    
    // Count total files first
    char start_msg[256];
    snprintf(start_msg, sizeof(start_msg), "📊 Scanning folder: %s", data->path);
    send_progress_message(start_msg);
    
    g_total_files = count_files_recursive(data->path);
    
    if (g_total_files == 0) {
        char empty_msg[256];
        snprintf(empty_msg, sizeof(empty_msg), "⚠️ Folder is empty or already deleted");
        send_progress_message(empty_msg);
        
        // Still try to delete the empty folder itself
        rmdir(data->path);
        
        // Send final OK response even for empty folders
        pthread_mutex_lock(&g_progress_mutex);
        if (g_client_sock > 0) {
            uint8_t header[5];
            header[0] = RESP_OK;
            uint32_t len = 0;
            memcpy(header + 1, &len, 4);
            send_all(g_client_sock, header, 5);
        }
        g_client_sock = 0;
        pthread_mutex_unlock(&g_progress_mutex);
        free(data);
        return NULL;
    }
    
    char count_msg[256];
    snprintf(count_msg, sizeof(count_msg), "📊 Total: %d files to delete", g_total_files);
    send_progress_message(count_msg);
    
    // Start deletion
    char del_msg[256];
    snprintf(del_msg, sizeof(del_msg), "🗑️ Starting deletion...");
    send_progress_message(del_msg);
    
    // Perform deletion in background
    int result = rmdir_recursive(data->path);
    
    // Send completion message
    if (result == 0) {
        char msg[256];
        snprintf(msg, sizeof(msg), "✅ Deleted %d files (100%%)", g_delete_count);
        send_progress_message(msg);
        send_notification(msg);
        
        // Send final OK response to signal completion
        pthread_mutex_lock(&g_progress_mutex);
        if (g_client_sock > 0) {
            uint8_t header[5];
            header[0] = RESP_OK;
            uint32_t len = 0;
            memcpy(header + 1, &len, 4);
            send_all(g_client_sock, header, 5);
        }
        pthread_mutex_unlock(&g_progress_mutex);
        
        // Wait for data to be sent before cleanup
        struct timespec ts;
        ts.tv_sec = 0;
        ts.tv_nsec = 200000000; // 200ms
        nanosleep(&ts, NULL);
    } else {
        char msg[256];
        snprintf(msg, sizeof(msg), "❌ Failed to delete folder (%d files removed)", g_delete_count);
        send_progress_message(msg);
        
        // Send error response
        pthread_mutex_lock(&g_progress_mutex);
        if (g_client_sock > 0) {
            uint8_t header[5];
            header[0] = RESP_ERROR;
            uint32_t len = 0;
            memcpy(header + 1, &len, 4);
            send_all(g_client_sock, header, 5);
        }
        pthread_mutex_unlock(&g_progress_mutex);
        
        // Wait for data to be sent before cleanup
        struct timespec ts;
        ts.tv_sec = 0;
        ts.tv_nsec = 200000000; // 200ms
        nanosleep(&ts, NULL);
    }
    
    g_client_sock = 0;
    free(data);
    return NULL;
}

// Handle DELETE_DIR - BUG FIX: Async deletion with progress reporting
void handle_delete_dir(client_session_t *session, const char *path) {
    // DO NOT send OK immediately - let background thread handle all responses
    // This prevents "Unexpected response: Data" error
    
    // Create background thread for deletion
    delete_thread_data_t* data = malloc(sizeof(delete_thread_data_t));
    if (data) {
        strncpy(data->path, path, MAX_PATH - 1);
        data->path[MAX_PATH - 1] = '\0';
        data->client_sock = session->sock;
        
        pthread_t thread;
        pthread_attr_t attr;
        pthread_attr_init(&attr);
        pthread_attr_setdetachstate(&attr, PTHREAD_CREATE_DETACHED);
        
        if (pthread_create(&thread, &attr, delete_thread_func, data) != 0) {
            // Thread creation failed, delete synchronously and send response
            free(data);
            g_client_sock = session->sock;
            int result = rmdir_recursive(path);
            if (result == 0) {
                send_ok(session->sock, "Folder deleted");
            } else {
                send_error(session->sock, "Failed to delete folder");
            }
            g_client_sock = 0;
        }
        
        pthread_attr_destroy(&attr);
    } else {
        // Malloc failed, delete synchronously and send response
        g_client_sock = session->sock;
        int result = rmdir_recursive(path);
        if (result == 0) {
            send_ok(session->sock, "Folder deleted");
        } else {
            send_error(session->sock, "Failed to delete folder");
        }
        g_client_sock = 0;
    }
}

// Handle RENAME
void handle_rename(client_session_t *session, const uint8_t *data, uint32_t data_len) {
    const char *old_path = (const char *)data;
    uint32_t old_len = strlen(old_path);
    if (old_len + 2 > data_len) {
        send_error(session->sock, "Invalid rename request");
        return;
    }
    const char *new_path = (const char *)(data + old_len + 1);
    
    char norm_old[MAX_PATH], norm_new[MAX_PATH];
    snprintf(norm_old, sizeof(norm_old), "%s", old_path);
    snprintf(norm_new, sizeof(norm_new), "%s", new_path);
    normalize_path(norm_old);
    normalize_path(norm_new);
    
    if (rename(norm_old, norm_new) == 0) {
        send_ok(session->sock, "Renamed successfully");
    } else {
        send_error(session->sock, "Failed to rename");
    }
}

// Handle COPY_FILE
void handle_copy_file(client_session_t *session, const uint8_t *data, uint32_t data_len) {
    const char *src = (const char *)data;
    uint32_t src_len = strlen(src);
    if (src_len + 2 > data_len) {
        send_error(session->sock, "Invalid copy request");
        return;
    }
    const char *dst = (const char *)(data + src_len + 1);
    
    char norm_src[MAX_PATH], norm_dst[MAX_PATH];
    snprintf(norm_src, sizeof(norm_src), "%s", src);
    snprintf(norm_dst, sizeof(norm_dst), "%s", dst);
    normalize_path(norm_src);
    normalize_path(norm_dst);
    
    int src_fd = open(norm_src, O_RDONLY);
    if (src_fd < 0) {
        send_error(session->sock, "Cannot open source file");
        return;
    }
    
    int dst_fd = open(norm_dst, O_WRONLY | O_CREAT | O_TRUNC, 0777);
    if (dst_fd < 0) {
        close(src_fd);
        send_error(session->sock, "Cannot create destination file");
        return;
    }
    
    char *buf = malloc(BUFFER_SIZE);
    if (!buf) {
        close(src_fd);
        close(dst_fd);
        send_error(session->sock, "Memory allocation failed");
        return;
    }
    
    ssize_t n;
    int success = 1;
    while ((n = read(src_fd, buf, BUFFER_SIZE)) > 0) {
        if (write(dst_fd, buf, n) != n) {
            success = 0;
            break;
        }
    }
    
    free(buf);
    close(src_fd);
    close(dst_fd);
    chmod(norm_dst, 0777);
    
    if (success) {
        send_ok(session->sock, "File copied");
    } else {
        send_error(session->sock, "Failed to copy file");
    }
}

// Handle MOVE_FILE
void handle_move_file(client_session_t *session, const uint8_t *data, uint32_t data_len) {
    const char *src = (const char *)data;
    uint32_t src_len = strlen(src);
    if (src_len + 2 > data_len) {
        send_error(session->sock, "Invalid move request");
        return;
    }
    const char *dst = (const char *)(data + src_len + 1);
    
    char norm_src[MAX_PATH], norm_dst[MAX_PATH];
    snprintf(norm_src, sizeof(norm_src), "%s", src);
    snprintf(norm_dst, sizeof(norm_dst), "%s", dst);
    normalize_path(norm_src);
    normalize_path(norm_dst);
    
    if (rename(norm_src, norm_dst) == 0) {
        send_ok(session->sock, "File moved");
    } else {
        send_error(session->sock, "Failed to move file");
    }
}

// Handle START_UPLOAD (with optional chunk offset for parallel upload)
void handle_start_upload(client_session_t *session, const uint8_t *data, uint32_t data_len) {
    if (session->upload_fd >= 0) {
        close(session->upload_fd);
        session->upload_fd = -1;
        // Release previous file mutex to prevent leak
        if (session->file_mutex) {
            release_file_mutex(session->upload_path);
            session->file_mutex = NULL;
        }
    }
    
    // Parse path, size, and optional offset
    const char *path = (const char *)data;
    uint32_t path_len = strlen(path);
    if (path_len + 9 > data_len) {
        send_error(session->sock, "Invalid upload request");
        return;
    }
    
    // Normalize path to remove double slashes
    char norm_path[MAX_PATH];
    snprintf(norm_path, sizeof(norm_path), "%s", path);
    normalize_path(norm_path);
    
    uint64_t file_size;
    memcpy(&file_size, data + path_len + 1, 8);
    
    // Check for optional offset (for chunked parallel upload)
    uint64_t chunk_offset = 0;
    if (path_len + 17 <= data_len) {
        memcpy(&chunk_offset, data + path_len + 9, 8);
    }
    
    // Create parent directories
    char parent[MAX_PATH];
    strncpy(parent, norm_path, sizeof(parent) - 1);
    parent[sizeof(parent) - 1] = '\0';
    char *last_slash = strrchr(parent, '/');
    if (last_slash) {
        *last_slash = '\0';
        mkdir_recursive(parent);
    }
    
    // Get per-file mutex for this specific file
    session->file_mutex = get_file_mutex(norm_path);
    if (!session->file_mutex) {
        send_error(session->sock, "Cannot allocate file mutex");
        return;
    }
    
    // CRITICAL: Lock mutex BEFORE opening file to prevent race condition
    // when multiple threads try to create the same file simultaneously
    pthread_mutex_lock(session->file_mutex);
    
    // Open file for writing using direct syscalls (faster than FILE*)
    // For chunked uploads, we need to pre-allocate the file on first chunk
    if (chunk_offset > 0) {
        // Subsequent chunk: open existing file
        session->upload_fd = open(norm_path, O_WRONLY);
        if (session->upload_fd >= 0) {
            // Seek to chunk offset
            lseek(session->upload_fd, chunk_offset, SEEK_SET);
        }
    } else {
        // First chunk or small file: create new file
        session->upload_fd = open(norm_path, O_WRONLY | O_CREAT | O_TRUNC, 0777);
        if (session->upload_fd >= 0 && file_size > 100 * 1024 * 1024) {
            // Large file - pre-allocate full size for chunked upload
            if (lseek(session->upload_fd, file_size - 1, SEEK_SET) < 0 || write(session->upload_fd, "", 1) != 1) {
                // Pre-allocation failed - likely disk full
                close(session->upload_fd);
                session->upload_fd = -1;
                pthread_mutex_unlock(session->file_mutex);
                release_file_mutex(norm_path);
                session->file_mutex = NULL;
                unlink(norm_path); // Remove partial file
                send_error(session->sock, "Disk full - cannot pre-allocate file");
                return;
            }
            
            // Seek back to beginning
            lseek(session->upload_fd, 0, SEEK_SET);
        }
    }
    
    pthread_mutex_unlock(session->file_mutex);
    
    if (session->upload_fd < 0) {
        release_file_mutex(norm_path);
        session->file_mutex = NULL;
        send_error(session->sock, "Cannot create file");
        return;
    }
    
    strncpy(session->upload_path, norm_path, sizeof(session->upload_path) - 1);
    session->upload_size = file_size;
    session->upload_received = chunk_offset;
    session->current_offset = chunk_offset;  // Set write offset for pwrite()
    
    // Increase socket receive buffer for this upload session
    int huge_buf = 16 * 1024 * 1024; // 16MB receive buffer - matches download optimization
    setsockopt(session->sock, SOL_SOCKET, SO_RCVBUF, &huge_buf, sizeof(huge_buf));
    
    send_response(session->sock, RESP_READY, NULL, 0);
}

// Handle UPLOAD_CHUNK
void handle_upload_chunk(client_session_t *session, const uint8_t *data, uint32_t data_len) {
    if (session->upload_fd < 0 || !session->file_mutex) {
        send_error(session->sock, "No upload in progress");
        return;
    }
    
    // Use pwrite() for TRUE PARALLEL WRITES - no mutex needed!
    // pwrite() is thread-safe and writes to specific offset without moving file position
    // This allows multiple connections to write different chunks simultaneously
    ssize_t written = pwrite(session->upload_fd, data, data_len, session->current_offset);
    
    if (written != data_len) {
        send_error(session->sock, "Write failed");
        close(session->upload_fd);
        session->upload_fd = -1;
        release_file_mutex(session->upload_path);
        session->file_mutex = NULL;
        return;
    }
    
    // Update current offset for next write
    session->current_offset += written;
    
    // Update total received bytes for progress tracking
    session->upload_received += written;
    
    // No response - zero blocking for maximum speed
}

// Handle END_UPLOAD
void handle_end_upload(client_session_t *session) {
    if (session->upload_fd < 0) {
        send_error(session->sock, "No upload in progress");
        return;
    }
    
    // Direct close syscall (no buffering to flush)
    close(session->upload_fd);
    session->upload_fd = -1;
    
    if (session->file_mutex) {
        release_file_mutex(session->upload_path);
        session->file_mutex = NULL;
    }
    
    chmod(session->upload_path, 0777);
    
    send_ok(session->sock, "Upload complete");
}

// Handle DOWNLOAD_FILE
void handle_download_file(client_session_t *session, const char *path) {
    char norm_path[MAX_PATH];
    snprintf(norm_path, sizeof(norm_path), "%s", path);
    normalize_path(norm_path);
    
    int fd = open(norm_path, O_RDONLY);
    if (fd < 0) {
        send_error(session->sock, "Cannot open file");
        return;
    }
    
    struct stat st;
    if (fstat(fd, &st) != 0) {
        close(fd);
        send_error(session->sock, "Cannot stat file");
        return;
    }
    
    // Send file size first
    uint64_t file_size = st.st_size;
    send_response(session->sock, RESP_DATA, &file_size, sizeof(file_size));
    
    // Manual read/write loop for maximum sustained throughput
    // FreeBSD sendfile has TCP congestion issues with large files
    char *buffer = malloc(8 * 1024 * 1024);
    if (!buffer) {
        close(fd);
        send_error(session->sock, "Out of memory");
        return;
    }
    
    ssize_t n;
    while ((n = read(fd, buffer, 8 * 1024 * 1024)) > 0) {
        ssize_t sent = 0;
        while (sent < n) {
            ssize_t s = send(session->sock, buffer + sent, n - sent, 0);
            if (s <= 0) {
                free(buffer);
                close(fd);
                return;
            }
            sent += s;
        }
    }
    
    free(buffer);
    
    close(fd);
}

// Handle SHELL_OPEN - Initialize shell session
// ============================================================================
// FILESYSTEM INDEXING SYSTEM
// ============================================================================

// Add entry to index
void index_add_entry(const char *path, const char *name, uint64_t size, time_t mtime, bool is_dir) {
    index_entry_t *entry = (index_entry_t*)malloc(sizeof(index_entry_t));
    if (!entry) return;
    
    strncpy(entry->path, path, sizeof(entry->path) - 1);
    entry->path[sizeof(entry->path) - 1] = '\0';
    strncpy(entry->name, name, sizeof(entry->name) - 1);
    entry->name[sizeof(entry->name) - 1] = '\0';
    entry->size = size;
    entry->mtime = mtime;
    entry->is_dir = is_dir;
    
    pthread_mutex_lock(&g_index.mutex);
    entry->next = g_index.entries;
    g_index.entries = entry;
    if (is_dir) {
        g_index.total_dirs++;
    } else {
        g_index.total_files++;
    }
    pthread_mutex_unlock(&g_index.mutex);
}

// Clear index
void index_clear() {
    pthread_mutex_lock(&g_index.mutex);
    index_entry_t *entry = g_index.entries;
    while (entry) {
        index_entry_t *next = entry->next;
        free(entry);
        entry = next;
    }
    g_index.entries = NULL;
    g_index.total_files = 0;
    g_index.total_dirs = 0;
    pthread_mutex_unlock(&g_index.mutex);
}

// Recursive filesystem scan
void index_scan_directory(const char *path) {
    DIR *dir = opendir(path);
    if (!dir) {
        // Log error but continue
        return;
    }
    
    struct dirent *entry;
    while ((entry = readdir(dir)) != NULL) {
        if (strcmp(entry->d_name, ".") == 0 || strcmp(entry->d_name, "..") == 0) {
            continue;
        }
        
        char fullpath[MAX_PATH];
        // Handle root path correctly (avoid double slash)
        if (strcmp(path, "/") == 0) {
            snprintf(fullpath, sizeof(fullpath), "/%s", entry->d_name);
        } else {
            snprintf(fullpath, sizeof(fullpath), "%s/%s", path, entry->d_name);
        }
        
        struct stat st;
        if (stat(fullpath, &st) == 0) {
            bool is_dir = S_ISDIR(st.st_mode);
            index_add_entry(fullpath, entry->d_name, st.st_size, st.st_mtime, is_dir);
            
            // Recurse into subdirectories (skip special dirs)
            if (is_dir) {
                // Skip problematic directories that may cause hangs
                if (strcmp(entry->d_name, "dev") != 0 &&
                    strcmp(entry->d_name, "proc") != 0 &&
                    strcmp(entry->d_name, "sys") != 0) {
                    index_scan_directory(fullpath);
                }
            }
        }
    }
    closedir(dir);
}

// Indexing thread
void* index_thread_func(void* arg) {
    const char **paths = (const char**)arg;
    
    pthread_mutex_lock(&g_index.mutex);
    g_index.indexing = true;
    g_index.ready = false;
    pthread_mutex_unlock(&g_index.mutex);
    
    // Clear old index
    index_clear();
    
    // Scan all provided paths
    for (int i = 0; paths[i] != NULL; i++) {
        index_scan_directory(paths[i]);
    }
    
    pthread_mutex_lock(&g_index.mutex);
    g_index.indexing = false;
    g_index.ready = true;
    pthread_mutex_unlock(&g_index.mutex);
    
    // Free individual strdup'd path strings, then the array itself
    for (int i = 0; paths[i] != NULL; i++) {
        free((void*)paths[i]);
    }
    free(paths);
    return NULL;
}

// Case-insensitive character comparison
static inline char to_lower(char c) {
    return (c >= 'A' && c <= 'Z') ? (c + 32) : c;
}

// Simple wildcard matching (supports * and ?) - case insensitive
bool wildcard_match(const char *pattern, const char *str) {
    while (*pattern && *str) {
        if (*pattern == '*') {
            pattern++;
            if (!*pattern) return true;
            while (*str) {
                if (wildcard_match(pattern, str)) return true;
                str++;
            }
            return false;
        } else if (*pattern == '?' || to_lower(*pattern) == to_lower(*str)) {
            pattern++;
            str++;
        } else {
            return false;
        }
    }
    return (*pattern == '\0' || (*pattern == '*' && *(pattern + 1) == '\0')) && *str == '\0';
}

// Parse size filter (e.g., ">1GB", "<100MB")
bool parse_size_filter(const char *filter, int64_t *min_size, int64_t *max_size) {
    if (!filter || strlen(filter) == 0) return false;
    
    char op = filter[0];
    if (op != '>' && op != '<') return false;
    
    char *endptr;
    double value = strtod(filter + 1, &endptr);
    if (endptr == filter + 1) return false;
    
    // Parse unit (KB, MB, GB)
    int64_t multiplier = 1;
    if (strcasecmp(endptr, "KB") == 0) {
        multiplier = 1024;
    } else if (strcasecmp(endptr, "MB") == 0) {
        multiplier = 1024 * 1024;
    } else if (strcasecmp(endptr, "GB") == 0) {
        multiplier = 1024 * 1024 * 1024;
    }
    
    int64_t size = (int64_t)(value * multiplier);
    
    if (op == '>') {
        *min_size = size;
    } else {
        *max_size = size;
    }
    
    return true;
}

// Search index with query
void handle_search_index(client_session_t *session, const char *query) {
    if (!g_index.ready) {
        send_error(session->sock, "Index not ready. Start indexing first.");
        return;
    }
    
    // Parse query: "*.pkg size:>1GB"
    char name_pattern[256];
    int64_t min_size = 0;
    int64_t max_size = INT64_MAX;
    bool has_pattern = false;
    
    // Simple query parser
    char query_copy[1024];
    strncpy(query_copy, query, sizeof(query_copy) - 1);
    query_copy[sizeof(query_copy) - 1] = '\0';
    
    char *token = strtok(query_copy, " ");
    while (token) {
        if (strncmp(token, "size:", 5) == 0) {
            parse_size_filter(token + 5, &min_size, &max_size);
        } else {
            strncpy(name_pattern, token, sizeof(name_pattern) - 1);
            name_pattern[sizeof(name_pattern) - 1] = '\0';
            has_pattern = true;
        }
        token = strtok(NULL, " ");
    }
    
    // If no pattern provided, default to "*" (match all)
    if (!has_pattern) {
        strcpy(name_pattern, "*");
    }
    
    // Search through index
    int result_count = 0;
    pthread_mutex_lock(&g_index.mutex);
    
    index_entry_t *entry = g_index.entries;
    while (entry && result_count < 1000) {  // Limit to 1000 results
        // Match name pattern (search in both name and full path)
        bool name_match = wildcard_match(name_pattern, entry->name);
        bool path_match = wildcard_match(name_pattern, entry->path);
        
        if (!name_match && !path_match) {
            entry = entry->next;
            continue;
        }
        
        // Match size filter
        if (entry->size < min_size || entry->size > max_size) {
            entry = entry->next;
            continue;
        }
        
        // Send result
        uint8_t resp = RESP_DATA;
        send(session->sock, &resp, 1, 0);
        
        // Send: path_len(4) + path + name_len(4) + name + size(8) + mtime(8) + is_dir(1)
        uint32_t path_len = strlen(entry->path);
        uint32_t name_len = strlen(entry->name);
        
        send(session->sock, &path_len, 4, 0);
        send(session->sock, entry->path, path_len, 0);
        send(session->sock, &name_len, 4, 0);
        send(session->sock, entry->name, name_len, 0);
        send(session->sock, &entry->size, 8, 0);
        send(session->sock, &entry->mtime, 8, 0);
        uint8_t is_dir = entry->is_dir ? 1 : 0;
        send(session->sock, &is_dir, 1, 0);
        
        result_count++;
        entry = entry->next;
    }
    
    pthread_mutex_unlock(&g_index.mutex);
    
    char msg[128];
    snprintf(msg, sizeof(msg), "Found %d results", result_count);
    send_ok(session->sock, msg);
}

// Start indexing
void handle_index_start(client_session_t *session, const char *paths_str) {
    if (g_index.indexing) {
        send_error(session->sock, "Indexing already in progress");
        return;
    }
    
    // Parse paths (comma-separated)
    const char **paths = (const char**)malloc(sizeof(char*) * 16);
    int path_count = 0;
    
    char paths_copy[1024];
    strncpy(paths_copy, paths_str, sizeof(paths_copy) - 1);
    paths_copy[sizeof(paths_copy) - 1] = '\0';
    
    char *token = strtok(paths_copy, ",");
    while (token && path_count < 15) {
        // Trim whitespace
        while (*token == ' ') token++;
        char *end = token + strlen(token) - 1;
        while (end > token && *end == ' ') *end-- = '\0';
        
        paths[path_count++] = strdup(token);
        token = strtok(NULL, ",");
    }
    paths[path_count] = NULL;
    
    // Start indexing thread
    if (pthread_create(&g_index.thread, NULL, index_thread_func, paths) != 0) {
        send_error(session->sock, "Failed to start indexing thread");
        for (int i = 0; i < path_count; i++) {
            free((void*)paths[i]);
        }
        free(paths);
        return;
    }
    
    pthread_detach(g_index.thread);
    send_ok(session->sock, "Indexing started");
}

// Get index status
void handle_index_status(client_session_t *session) {
    pthread_mutex_lock(&g_index.mutex);
    
    char status[256];
    if (g_index.indexing) {
        snprintf(status, sizeof(status), "Indexing: %d files, %d dirs", 
                 g_index.total_files, g_index.total_dirs);
    } else if (g_index.ready) {
        snprintf(status, sizeof(status), "Ready: %d files, %d dirs indexed", 
                 g_index.total_files, g_index.total_dirs);
    } else {
        snprintf(status, sizeof(status), "Not started");
    }
    
    pthread_mutex_unlock(&g_index.mutex);
    
    send_ok(session->sock, status);
}

// ============================================================================
// GAME MOUNTING HANDLER
// ============================================================================

static int remount_system_ex(void) {
    struct iovec iov[] = {
        IOVEC_ENTRY("from"),      IOVEC_ENTRY("/dev/ssd0.system_ex"),
        IOVEC_ENTRY("fspath"),    IOVEC_ENTRY("/system_ex"),
        IOVEC_ENTRY("fstype"),    IOVEC_ENTRY("exfatfs"),
        IOVEC_ENTRY("large"),     IOVEC_ENTRY("yes"),
        IOVEC_ENTRY("timezone"),  IOVEC_ENTRY("static"),
        IOVEC_ENTRY("async"),     { NULL, 0 },
        IOVEC_ENTRY("ignoreacl"), { NULL, 0 },
    };
    return nmount(iov, IOVEC_SIZE(iov), MNT_UPDATE);
}

void handle_mount_games(client_session_t *session) {
    g_mounted_count = 0;
    
    // Initialize app install utility
    sceAppInstUtilInitialize();
    
    // Remount /system_ex as writable
    remount_system_ex();
    
    int cleaned = auto_unmount_deleted_games();

    int mounted_count = 0;
    int skipped_count = 0;
    int failed_count = 0;
    int duplicate_count = 0;
    int total_games = 0;
    int current_game = 0;
    struct stat st;
    
    // Store mounted game names for response (up to 20)
    char mounted_games[20][300];
    int stored_names = 0;

    // First pass: count total games across all paths
    for (int pi = 0; pi < (int)NUM_GAME_SCAN_PATHS; pi++) {
        if (stat(GAME_SCAN_PATHS[pi], &st) != 0 || !S_ISDIR(st.st_mode))
            continue;
        DIR* d = opendir(GAME_SCAN_PATHS[pi]);
        if (!d) continue;
        struct dirent* e;
        while ((e = readdir(d))) {
            if (!strcmp(e->d_name, ".") || !strcmp(e->d_name, "..")) continue;
            char gp[PATH_MAX];
            snprintf(gp, sizeof(gp), "%s/%s", GAME_SCAN_PATHS[pi], e->d_name);
            if (stat(gp, &st) == 0 && S_ISDIR(st.st_mode)) total_games++;
        }
        closedir(d);
    }

    // Send progress: starting
    {
        char prog_msg[256];
        snprintf(prog_msg, sizeof(prog_msg), "Scanning %d games across %d locations...", 
                 total_games, (int)NUM_GAME_SCAN_PATHS);
        send_notification(prog_msg);
    }

    // Second pass: process all games
    for (int pi = 0; pi < (int)NUM_GAME_SCAN_PATHS; pi++) {
        const char* base_path = GAME_SCAN_PATHS[pi];
        if (stat(base_path, &st) != 0 || !S_ISDIR(st.st_mode))
            continue;
        
        DIR* d = opendir(base_path);
        if (!d) continue;
        
        struct dirent* e;
        while ((e = readdir(d))) {
            if (!strcmp(e->d_name, ".") || !strcmp(e->d_name, "..")) continue;
            
            char game_path[PATH_MAX];
            snprintf(game_path, sizeof(game_path), "%s/%s", base_path, e->d_name);
            if (stat(game_path, &st) != 0 || !S_ISDIR(st.st_mode)) continue;
            
            current_game++;
            char game_name[256] = {};
            char title_id[12] = {};
            int result = process_game(game_path, game_name, sizeof(game_name), title_id, sizeof(title_id));
            
            if (result == 0) {
                // Successfully mounted
                if (stored_names < 20) {
                    snprintf(mounted_games[stored_names], sizeof(mounted_games[0]), "%s", game_name);
                    stored_names++;
                }
                mounted_count++;
                // Send PS5 notification for each mounted game
                char notify_msg[512];
                snprintf(notify_msg, sizeof(notify_msg), "Mounting %d/%d (%d%%)\n%s", 
                         current_game, total_games, 
                         total_games > 0 ? (current_game * 100) / total_games : 0,
                         game_name);
                send_notification(notify_msg);
            } else if (result == 2) {
                skipped_count++;
            } else if (result == 3) {
                duplicate_count++;
            } else {
                failed_count++;
            }
        }
        closedir(d);
    }

    // Build response message for client
    char response[8192];
    int off = 0;
    
    if (cleaned > 0) {
        off += snprintf(response + off, sizeof(response) - off, 
                       "Cleaned: %d deleted game(s)\n", cleaned);
    }
    
    off += snprintf(response + off, sizeof(response) - off,
                   "New mounts: %d\n", mounted_count);
    
    if (mounted_count > 0 && stored_names > 0) {
        off += snprintf(response + off, sizeof(response) - off, "Mounted games:\n");
        for (int i = 0; i < stored_names && off < (int)sizeof(response) - 100; i++) {
            off += snprintf(response + off, sizeof(response) - off,
                          "  %s\n", mounted_games[i]);
        }
    }
    
    off += snprintf(response + off, sizeof(response) - off,
                   "Already mounted: %d\n", skipped_count);
    if (duplicate_count > 0) {
        off += snprintf(response + off, sizeof(response) - off,
                       "Duplicates skipped: %d\n", duplicate_count);
    }
    off += snprintf(response + off, sizeof(response) - off,
                   "Failed: %d\nTotal active: %d",
                   failed_count, mounted_count + skipped_count);

    // Send final PS5 notification
    if (mounted_count > 0) {
        char msg[2048];
        snprintf(msg, sizeof(msg), "Game Mounter\nMounted %d new game(s)\n%d already mounted",
                 mounted_count, skipped_count);
        send_notification(msg);
    } else if (skipped_count > 0) {
        char msg[256];
        snprintf(msg, sizeof(msg), "Game Mounter\nAll %d game(s) already mounted", skipped_count);
        send_notification(msg);
    } else {
        send_notification("Game Mounter\nNo games found to mount");
    }

    send_ok(session->sock, response);
}

// ============================================================================
// HARDWARE & SYSTEM INFO FUNCTIONS
// ============================================================================

// Handle GET_GAME_LIST - Get list of all mounted games with details
void handle_get_game_list(client_session_t *session) {
    char *response = malloc(65536);  // 64KB for game list
    if (!response) {
        send_error(session->sock, "Out of memory");
        return;
    }
    
    int off = 0;
    int game_count = 0;
    
    // Scan /user/app for mounted games (those with mount.lnk)
    DIR *d = opendir("/user/app");
    if (!d) {
        send_error(session->sock, "Cannot open /user/app");
        free(response);
        return;
    }
    
    struct dirent *e;
    while ((e = readdir(d)) && off < 60000) {
        if (!strcmp(e->d_name, ".") || !strcmp(e->d_name, ".."))
            continue;
        
        // Check if it's a valid title ID format (CUSA/PPSA + 5 digits)
        if ((strncmp(e->d_name, "CUSA", 4) != 0 && 
             strncmp(e->d_name, "PPSA", 4) != 0) || 
            strlen(e->d_name) != 9)
            continue;
        
        // Check if mount.lnk exists (indicates it's a mounted game)
        char mount_lnk[PATH_MAX];
        snprintf(mount_lnk, sizeof(mount_lnk), "/user/app/%s/mount.lnk", e->d_name);
        
        FILE *f = fopen(mount_lnk, "r");
        if (!f) continue;  // Not a mounted game
        
        char game_path[PATH_MAX];
        memset(game_path, 0, sizeof(game_path));
        if (!fgets(game_path, sizeof(game_path), f)) {
            fclose(f);
            continue;
        }
        fclose(f);
        game_path[strcspn(game_path, "\r\n")] = '\0';
        
        // Get game name from param.json
        char param_json[PATH_MAX];
        snprintf(param_json, sizeof(param_json), "%s/sce_sys/param.json", game_path);
        char game_name[256] = "Unknown";
        get_game_name_from_json(param_json, game_name, sizeof(game_name));
        
        // Get game size (approximate from eboot.bin)
        char eboot_path[PATH_MAX];
        snprintf(eboot_path, sizeof(eboot_path), "%s/eboot.bin", game_path);
        struct stat st;
        uint64_t game_size = 0;
        if (stat(eboot_path, &st) == 0) {
            game_size = st.st_size;
        }
        
        // Get region
        const char *region = get_game_region(e->d_name);
        
        // Check if nullfs mount is active
        char system_ex_app[PATH_MAX];
        snprintf(system_ex_app, sizeof(system_ex_app), "/system_ex/app/%s", e->d_name);
        int is_active = is_mounted(system_ex_app);
        
        // Format: title_id|name|path|size|region|active
        off += snprintf(response + off, 65536 - off,
            "%s|%s|%s|%llu|%s|%d\n",
            e->d_name, game_name, game_path, 
            (unsigned long long)game_size, region, is_active);
        
        game_count++;
    }
    closedir(d);
    
    if (game_count == 0) {
        strcpy(response, "NO_GAMES\n");
        off = strlen(response);
    }
    
    send_response(session->sock, RESP_DATA, response, off);
    free(response);
}

// Handle GET_GAME_ICON - Send icon0.png binary for a given title_id
void handle_get_game_icon(client_session_t *session, const char *title_id) {
    if (!title_id || strlen(title_id) == 0) {
        send_error(session->sock, "No title ID provided");
        return;
    }
    
    // Validate title ID format
    if ((strncmp(title_id, "CUSA", 4) != 0 && 
         strncmp(title_id, "PPSA", 4) != 0) || 
        strlen(title_id) != 9) {
        send_error(session->sock, "Invalid title ID format");
        return;
    }
    
    // Try multiple icon paths (prefer appmeta, then user/app, then game source)
    char icon_path[PATH_MAX];
    FILE *f = NULL;
    
    // 1. /user/appmeta/{title_id}/icon0.png
    snprintf(icon_path, sizeof(icon_path), "/user/appmeta/%s/icon0.png", title_id);
    f = fopen(icon_path, "rb");
    
    // 2. /user/app/{title_id}/sce_sys/icon0.png
    if (!f) {
        snprintf(icon_path, sizeof(icon_path), "/user/app/%s/sce_sys/icon0.png", title_id);
        f = fopen(icon_path, "rb");
    }
    
    // 3. Follow mount.lnk to the original game path
    if (!f) {
        char mount_lnk[PATH_MAX];
        snprintf(mount_lnk, sizeof(mount_lnk), "/user/app/%s/mount.lnk", title_id);
        FILE *lnk = fopen(mount_lnk, "r");
        if (lnk) {
            char game_path[PATH_MAX] = {0};
            if (fgets(game_path, sizeof(game_path), lnk)) {
                game_path[strcspn(game_path, "\r\n")] = '\0';
                snprintf(icon_path, sizeof(icon_path), "%s/sce_sys/icon0.png", game_path);
                f = fopen(icon_path, "rb");
            }
            fclose(lnk);
        }
    }
    
    // 4. Scan /user/home/*/savedata_prospero_meta/user/{title_id}/ for any *_icon0.png
    //    This covers saves for games that are no longer installed/mounted.
    if (!f) {
        DIR *home_dir = opendir("/user/home");
        if (home_dir) {
            struct dirent *user_ent;
            while ((user_ent = readdir(home_dir)) && !f) {
                if (!strcmp(user_ent->d_name, ".") || !strcmp(user_ent->d_name, "..")) continue;
                
                char meta_dir[PATH_MAX];
                snprintf(meta_dir, sizeof(meta_dir),
                         "/user/home/%s/savedata_prospero_meta/user/%s",
                         user_ent->d_name, title_id);
                
                DIR *md = opendir(meta_dir);
                if (!md) continue;
                
                struct dirent *file_ent;
                while ((file_ent = readdir(md))) {
                    // Pick any file ending in "_icon0.png"
                    size_t nlen = strlen(file_ent->d_name);
                    if (nlen > 10 && 
                        strstr(file_ent->d_name, "icon0.png") != NULL) {
                        snprintf(icon_path, sizeof(icon_path),
                                 "%s/%s", meta_dir, file_ent->d_name);
                        f = fopen(icon_path, "rb");
                        if (f) break;
                    }
                }
                closedir(md);
            }
            closedir(home_dir);
        }
    }
    
    if (!f) {
        send_error(session->sock, "Icon not found");
        return;
    }
    
    // Get file size
    fseek(f, 0, SEEK_END);
    long icon_size = ftell(f);
    fseek(f, 0, SEEK_SET);
    
    if (icon_size <= 0 || icon_size > 2 * 1024 * 1024) {  // Max 2MB
        fclose(f);
        send_error(session->sock, "Invalid icon size");
        return;
    }
    
    char *icon_data = malloc(icon_size);
    if (!icon_data) {
        fclose(f);
        send_error(session->sock, "Out of memory");
        return;
    }
    
    size_t read_bytes = fread(icon_data, 1, icon_size, f);
    fclose(f);
    
    if ((long)read_bytes != icon_size) {
        free(icon_data);
        send_error(session->sock, "Failed to read icon");
        return;
    }
    
    send_response(session->sock, RESP_DATA, icon_data, icon_size);
    free(icon_data);
}

// Handle GET_GAME_PIC - Send pic0.png or pic1.png binary for a given title_id
// Request format: "TITLE_ID:PIC_TYPE" where PIC_TYPE is '0' for pic0 or '1' for pic1
void handle_get_game_pic(client_session_t *session, const char *request) {
    if (!request || strlen(request) < 11) {  // Need at least TITLE_ID:X
        send_error(session->sock, "Invalid request format");
        return;
    }
    
    // Parse title_id and pic type
    char title_id[16] = {0};
    char pic_type = '0';
    
    // Find colon separator
    const char *colon = strchr(request, ':');
    if (!colon || (colon - request) != 9) {
        send_error(session->sock, "Invalid request format (expected TITLE_ID:TYPE)");
        return;
    }
    
    strncpy(title_id, request, 9);
    title_id[9] = '\0';
    pic_type = colon[1];
    
    // Validate title ID
    if ((strncmp(title_id, "CUSA", 4) != 0 && 
         strncmp(title_id, "PPSA", 4) != 0) || 
        strlen(title_id) != 9) {
        send_error(session->sock, "Invalid title ID format");
        return;
    }
    
    // Validate pic type
    if (pic_type != '0' && pic_type != '1') {
        send_error(session->sock, "Invalid pic type (use 0 or 1)");
        return;
    }
    
    char pic_filename[16];
    snprintf(pic_filename, sizeof(pic_filename), "pic%c.png", pic_type);
    
    // Try multiple paths
    char pic_path[PATH_MAX];
    FILE *f = NULL;
    
    // 1. /user/app/{title_id}/sce_sys/picN.png
    snprintf(pic_path, sizeof(pic_path), "/user/app/%s/sce_sys/%s", title_id, pic_filename);
    f = fopen(pic_path, "rb");
    
    // 2. /user/appmeta/{title_id}/picN.png
    if (!f) {
        snprintf(pic_path, sizeof(pic_path), "/user/appmeta/%s/%s", title_id, pic_filename);
        f = fopen(pic_path, "rb");
    }
    
    // 3. Follow mount.lnk to original source
    if (!f) {
        char mount_lnk[PATH_MAX];
        snprintf(mount_lnk, sizeof(mount_lnk), "/user/app/%s/mount.lnk", title_id);
        FILE *lnk = fopen(mount_lnk, "r");
        if (lnk) {
            char game_path[PATH_MAX] = {0};
            if (fgets(game_path, sizeof(game_path), lnk)) {
                game_path[strcspn(game_path, "\r\n")] = '\0';
                snprintf(pic_path, sizeof(pic_path), "%s/sce_sys/%s", game_path, pic_filename);
                f = fopen(pic_path, "rb");
            }
            fclose(lnk);
        }
    }
    
    if (!f) {
        send_error(session->sock, "Picture not found");
        return;
    }
    
    fseek(f, 0, SEEK_END);
    long pic_size = ftell(f);
    fseek(f, 0, SEEK_SET);
    
    // pic1 can be larger (up to 8MB, 3840x2160)
    if (pic_size <= 0 || pic_size > 16 * 1024 * 1024) {
        fclose(f);
        send_error(session->sock, "Invalid picture size");
        return;
    }
    
    char *pic_data = malloc(pic_size);
    if (!pic_data) {
        fclose(f);
        send_error(session->sock, "Out of memory");
        return;
    }
    
    size_t read_bytes = fread(pic_data, 1, pic_size, f);
    fclose(f);
    
    if ((long)read_bytes != pic_size) {
        free(pic_data);
        send_error(session->sock, "Failed to read picture");
        return;
    }
    
    send_response(session->sock, RESP_DATA, pic_data, pic_size);
    free(pic_data);
}

// Helper: Read text file contents into buffer (returns bytes read, 0 on error)
static size_t read_file_text(const char *path, char *buf, size_t buflen) {
    FILE *f = fopen(path, "r");
    if (!f) return 0;
    size_t n = fread(buf, 1, buflen - 1, f);
    fclose(f);
    buf[n] = '\0';
    return n;
}

// Helper: Recursively calculate directory size
static uint64_t dir_size_recursive(const char *path) {
    uint64_t total = 0;
    DIR *d = opendir(path);
    if (!d) return 0;
    
    struct dirent *e;
    while ((e = readdir(d))) {
        if (!strcmp(e->d_name, ".") || !strcmp(e->d_name, "..")) continue;
        
        char full[PATH_MAX];
        snprintf(full, sizeof(full), "%s/%s", path, e->d_name);
        struct stat st;
        if (lstat(full, &st) != 0) continue;
        
        if (S_ISDIR(st.st_mode)) {
            total += dir_size_recursive(full);
        } else if (S_ISREG(st.st_mode)) {
            total += st.st_size;
        }
    }
    closedir(d);
    return total;
}

// Handle GET_GAME_DETAILS - Send detailed info about a game (version, size, paths, etc)
void handle_get_game_details(client_session_t *session, const char *title_id) {
    if (!title_id || strlen(title_id) == 0) {
        send_error(session->sock, "No title ID provided");
        return;
    }
    
    if ((strncmp(title_id, "CUSA", 4) != 0 && 
         strncmp(title_id, "PPSA", 4) != 0) || 
        strlen(title_id) != 9) {
        send_error(session->sock, "Invalid title ID format");
        return;
    }
    
    // Read mount.lnk to get game source path
    char mount_lnk[PATH_MAX];
    snprintf(mount_lnk, sizeof(mount_lnk), "/user/app/%s/mount.lnk", title_id);
    char game_path[PATH_MAX] = {0};
    FILE *lnk = fopen(mount_lnk, "r");
    if (lnk) {
        if (fgets(game_path, sizeof(game_path), lnk)) {
            game_path[strcspn(game_path, "\r\n")] = '\0';
        }
        fclose(lnk);
    }
    
    char response[8192];
    int off = 0;
    
    // Game name from param.json
    char param_json[PATH_MAX];
    snprintf(param_json, sizeof(param_json), "%s/sce_sys/param.json", game_path);
    char game_name[256] = "Unknown";
    get_game_name_from_json(param_json, game_name, sizeof(game_name));
    
    // Read full param.json contents
    char param_content[4096] = {0};
    read_file_text(param_json, param_content, sizeof(param_content));
    
    // Escape newlines in param_content for single-line response
    for (char *p = param_content; *p; p++) {
        if (*p == '\n') *p = ' ';
        else if (*p == '\r') *p = ' ';
    }
    
    // Total game size (recursive)
    uint64_t total_size = 0;
    if (game_path[0]) {
        total_size = dir_size_recursive(game_path);
    }
    
    // EBoot size
    char eboot_path[PATH_MAX];
    snprintf(eboot_path, sizeof(eboot_path), "%s/eboot.bin", game_path);
    struct stat st;
    uint64_t eboot_size = 0;
    if (stat(eboot_path, &st) == 0) {
        eboot_size = st.st_size;
    }
    
    // Mount status
    char system_ex_app[PATH_MAX];
    snprintf(system_ex_app, sizeof(system_ex_app), "/system_ex/app/%s", title_id);
    int is_active = is_mounted(system_ex_app);
    
    // Get modification time of game directory (install date)
    time_t install_date = 0;
    if (game_path[0] && stat(game_path, &st) == 0) {
        install_date = st.st_mtime;
    }
    
    // Format timestamps
    char install_date_str[64] = "Unknown";
    if (install_date > 0) {
        struct tm *tm = localtime(&install_date);
        if (tm) {
            strftime(install_date_str, sizeof(install_date_str), "%Y-%m-%d %H:%M:%S", tm);
        }
    }
    
    // Region
    const char *region = get_game_region(title_id);
    
    off += snprintf(response + off, sizeof(response) - off,
        "title_id=%s\n"
        "name=%s\n"
        "path=%s\n"
        "region=%s\n"
        "total_size=%llu\n"
        "eboot_size=%llu\n"
        "install_date=%s\n"
        "is_active=%d\n"
        "param_json=%s\n",
        title_id,
        game_name,
        game_path,
        region,
        (unsigned long long)total_size,
        (unsigned long long)eboot_size,
        install_date_str,
        is_active,
        param_content);
    
    send_response(session->sock, RESP_DATA, response, off);
}

// Handle LIST_SAVES - Scan /user/home/*/savedata/* and return list of saves
// Format: title_id|user_id|save_path|total_size|mtime_unix\n...
void handle_list_saves(client_session_t *session) {
    char *response = malloc(65536);
    if (!response) {
        send_error(session->sock, "Out of memory");
        return;
    }
    
    int off = 0;
    int save_count = 0;
    
    DIR *home = opendir("/user/home");
    if (!home) {
        send_error(session->sock, "Cannot open /user/home");
        free(response);
        return;
    }
    
    // PS5 saves are in `savedata_prospero`, PS4 legacy in `savedata`
    const char *save_dirs[] = { "savedata_prospero", "savedata" };
    
    struct dirent *user_entry;
    while ((user_entry = readdir(home)) && off < 60000) {
        if (!strcmp(user_entry->d_name, ".") || !strcmp(user_entry->d_name, ".."))
            continue;
        
        // Try both savedata dirs for this user
        for (int si = 0; si < 2; si++) {
            char savedata_path[PATH_MAX];
            snprintf(savedata_path, sizeof(savedata_path),
                     "/user/home/%s/%s", user_entry->d_name, save_dirs[si]);
            
            DIR *sdir = opendir(savedata_path);
            if (!sdir) continue;
            
            struct dirent *save_entry;
            while ((save_entry = readdir(sdir)) && off < 60000) {
                if (!strcmp(save_entry->d_name, ".") || !strcmp(save_entry->d_name, ".."))
                    continue;
                
                // Filter by CUSA/PPSA title_id format (9 chars)
                if ((strncmp(save_entry->d_name, "CUSA", 4) != 0 && 
                     strncmp(save_entry->d_name, "PPSA", 4) != 0) || 
                    strlen(save_entry->d_name) != 9) {
                    continue;
                }
                
                char full_save_path[PATH_MAX];
                snprintf(full_save_path, sizeof(full_save_path),
                         "%s/%s", savedata_path, save_entry->d_name);
                
                struct stat st;
                if (stat(full_save_path, &st) != 0 || !S_ISDIR(st.st_mode)) {
                    continue;
                }
                
                uint64_t save_size = dir_size_recursive(full_save_path);
                
                off += snprintf(response + off, 65536 - off,
                    "%s|%s|%s|%llu|%lld\n",
                    save_entry->d_name,      // title_id
                    user_entry->d_name,      // user_id
                    full_save_path,          // save_path
                    (unsigned long long)save_size,
                    (long long)st.st_mtime);
                
                save_count++;
            }
            closedir(sdir);
        }
    }
    closedir(home);
    
    if (save_count == 0) {
        strcpy(response, "NO_SAVES\n");
        off = strlen(response);
    }
    
    send_response(session->sock, RESP_DATA, response, off);
    free(response);
}

// Handle LAUNCH_GAME - Launch an installed/mounted game by title ID
void handle_launch_game(client_session_t *session, const char *title_id) {
    if (!title_id || strlen(title_id) == 0) {
        send_error(session->sock, "No title ID provided");
        return;
    }
    
    // Validate title ID (CUSA/PPSA + 5 digits)
    if ((strncmp(title_id, "CUSA", 4) != 0 &&
         strncmp(title_id, "PPSA", 4) != 0) ||
        strlen(title_id) != 9) {
        send_error(session->sock, "Invalid title ID format");
        return;
    }
    
    // Initialize services (idempotent - safe to call even if already initialized)
    sceUserServiceInitialize(NULL);
    sceLncUtilInitialize();
    
    // Log the foreground user for debugging
    int fg_user = 0;
    sceUserServiceGetForegroundUser(&fg_user);
    
    // Allocate a properly sized LncAppParam struct on stack (zero-initialized).
    // The struct's first 4 bytes are typically the 'size' field.
    // Homebrew launchers typically pass either NULL or a 256-byte zeroed block.
    char lnc_param[256];
    memset(lnc_param, 0, sizeof(lnc_param));
    *(uint32_t *)lnc_param = sizeof(lnc_param);  // size field
    
    // Attempt 1: sceLncUtilLaunchApp with zero-initialized param
    int ret = sceLncUtilLaunchApp(title_id, NULL, lnc_param);
    int ret_lnc_null = 0, ret_sys = 0;
    
    // Attempt 2: sceLncUtilLaunchApp with NULL param
    if (ret < 0) {
        ret_lnc_null = sceLncUtilLaunchApp(title_id, NULL, NULL);
        if (ret_lnc_null >= 0) ret = ret_lnc_null;
    }
    
    // Attempt 3: sceSystemServiceLaunchApp fallback
    if (ret < 0) {
        ret_sys = sceSystemServiceLaunchApp(title_id, NULL, NULL);
        if (ret_sys == 0) ret = 0;
    }
    
    char msg[512];
    if (ret >= 0) {
        snprintf(msg, sizeof(msg), "Launched %s (app_id=%d)", title_id, ret);
        send_ok(session->sock, msg);
    } else {
        snprintf(msg, sizeof(msg),
                 "Launch failed. fg_user=0x%x  lnc_param=0x%x  lnc_null=0x%x  sys=0x%x",
                 fg_user, ret, ret_lnc_null, ret_sys);
        send_error(session->sock, msg);
    }
}

// Helper: scan a directory tree for images up to 2 levels deep and append to response.
// Returns number of screenshots added. (Limited depth to avoid runaway scans.)
static int scan_images_recursive(const char *path, int depth, char *response, int *off, int max_len) {
    if (depth > 5) return 0;  // av_contents/thumbnails/photo/NPXS40087/NPXS40087/{bucket}/file = 5 levels
    
    DIR *d = opendir(path);
    if (!d) return 0;
    
    int added = 0;
    struct dirent *e;
    while ((e = readdir(d))) {
        if (!strcmp(e->d_name, ".") || !strcmp(e->d_name, "..")) continue;
        
        char full[PATH_MAX];
        snprintf(full, sizeof(full), "%s/%s", path, e->d_name);
        
        struct stat st;
        if (stat(full, &st) != 0) continue;
        
        if (S_ISDIR(st.st_mode)) {
            added += scan_images_recursive(full, depth + 1, response, off, max_len);
        } else if (S_ISREG(st.st_mode)) {
            size_t nlen = strlen(e->d_name);
            int is_image =
                (nlen > 4 && (
                    !strcasecmp(e->d_name + nlen - 4, ".jpg") ||
                    !strcasecmp(e->d_name + nlen - 4, ".png") ||
                    !strcasecmp(e->d_name + nlen - 4, ".bmp"))) ||
                (nlen > 5 && !strcasecmp(e->d_name + nlen - 5, ".jpeg"));
            
            if (!is_image) continue;
            
            int written = snprintf(response + *off, max_len - *off,
                                   "%s|%s|%llu|%lld\n",
                                   full, e->d_name,
                                   (unsigned long long)st.st_size,
                                   (long long)st.st_mtime);
            if (written > 0 && *off + written < max_len) {
                *off += written;
                added++;
            }
        }
    }
    closedir(d);
    return added;
}

// Handle DELETE_SCREENSHOT - Delete all files belonging to a PS5 screenshot.
//
// PS5 screenshot layout (current firmware):
//   /user/av_contents/photo/NPXS40087/NPXS40087/{bucket}/YYYYMMDD_hhmmss_xxxxxxxx.dat   (encrypted original)
//   /user/av_contents/photo/NPXS40087/NPXS40087/{bucket}/YYYYMMDD_hhmmss_xxxxxxxx.meta  (metadata)
//   /user/av_contents/thumbnails/photo/NPXS40087/NPXS40087/{bucket}/YYYYMMDD_hhmmss_xxxxxxxx.jpg.jpeg
//                                                                         (visible thumbnail - what the client lists)
//
// The client usually sends us the .jpg.jpeg path (what it saw).
// We need to delete all three files: thumbnail + .dat + .meta.
void handle_delete_screenshot(client_session_t *session, const char *full_path) {
    if (!full_path || strlen(full_path) == 0) {
        send_error(session->sock, "No path provided");
        return;
    }
    
    // Safety: only allow paths under known screenshot roots
    if (strncmp(full_path, "/user/av_contents/", 18) != 0 &&
        strncmp(full_path, "/user/home/", 11) != 0) {
        send_error(session->sock, "Path not in allowed screenshot directory");
        return;
    }
    
    int removed = 0;
    
    // 1. Delete whatever file the client pointed us at
    if (unlink(full_path) == 0) removed++;
    
    // 2. Derive sibling paths.
    //    Case A: input is thumbnail  "…/thumbnails/photo/…/xxx.jpg.jpeg"
    //            → delete "…/photo/…/xxx.dat" and "…/photo/…/xxx.meta"
    //    Case B: input is original   "…/photo/…/xxx.dat" or ".meta"
    //            → delete corresponding thumbnail "…/thumbnails/photo/…/xxx.jpg.jpeg"
    //            → also delete the sibling (.dat/.meta pair)
    
    const char *thumb_root = strstr(full_path, "/av_contents/thumbnails/photo/");
    const char *orig_root  = strstr(full_path, "/av_contents/photo/");
    // (orig_root will also match if thumb_root is present; we handle that via precedence)
    
    // Extract directory + basename for transformations
    char dir_part[PATH_MAX] = {0};
    char base_part[PATH_MAX] = {0};
    const char *last_slash = strrchr(full_path, '/');
    if (!last_slash) {
        char m[64];
        snprintf(m, sizeof(m), "Deleted %d file(s)", removed);
        send_ok(session->sock, m);
        return;
    }
    size_t dir_len = last_slash - full_path;
    memcpy(dir_part, full_path, dir_len);
    dir_part[dir_len] = '\0';
    strncpy(base_part, last_slash + 1, sizeof(base_part) - 1);
    
    // Strip any known extension suffix to get the base stem
    char stem[PATH_MAX] = {0};
    strncpy(stem, base_part, sizeof(stem) - 1);
    const char *suffixes[] = { ".jpg.jpeg", ".jpeg", ".jpg", ".png", ".dat", ".meta" };
    for (size_t i = 0; i < sizeof(suffixes)/sizeof(suffixes[0]); i++) {
        size_t slen = strlen(suffixes[i]);
        size_t blen = strlen(stem);
        if (blen > slen && !strcasecmp(stem + blen - slen, suffixes[i])) {
            stem[blen - slen] = '\0';
            break;
        }
    }
    
    char sibling[PATH_MAX];
    
    if (thumb_root) {
        // Input is a thumbnail. Build original dir by removing "thumbnails/" segment.
        size_t keep = thumb_root - full_path;  // up to "/av_contents"
        char orig_dir[PATH_MAX];
        snprintf(orig_dir, sizeof(orig_dir), "%.*s/av_contents/photo/%s",
                 (int)keep, full_path,
                 dir_part + keep + strlen("/av_contents/thumbnails/photo/"));
        
        // Delete .dat and .meta siblings
        snprintf(sibling, sizeof(sibling), "%s/%s.dat", orig_dir, stem);
        if (unlink(sibling) == 0) removed++;
        snprintf(sibling, sizeof(sibling), "%s/%s.meta", orig_dir, stem);
        if (unlink(sibling) == 0) removed++;
    } else if (orig_root) {
        // Input is an original (.dat/.meta). Delete the paired file and the thumbnail.
        snprintf(sibling, sizeof(sibling), "%s/%s.dat", dir_part, stem);
        if (strcmp(sibling, full_path) != 0 && unlink(sibling) == 0) removed++;
        snprintf(sibling, sizeof(sibling), "%s/%s.meta", dir_part, stem);
        if (strcmp(sibling, full_path) != 0 && unlink(sibling) == 0) removed++;
        
        // Build thumbnail dir by inserting "thumbnails/" before "photo/"
        size_t keep = orig_root - full_path;  // up to "/av_contents"
        char thumb_dir[PATH_MAX];
        snprintf(thumb_dir, sizeof(thumb_dir), "%.*s/av_contents/thumbnails/photo/%s",
                 (int)keep, full_path,
                 dir_part + keep + strlen("/av_contents/photo/"));
        
        snprintf(sibling, sizeof(sibling), "%s/%s.jpg.jpeg", thumb_dir, stem);
        if (unlink(sibling) == 0) removed++;
        snprintf(sibling, sizeof(sibling), "%s/%s.jpeg", thumb_dir, stem);
        if (unlink(sibling) == 0) removed++;
    }
    
    char msg[256];
    snprintf(msg, sizeof(msg), "Deleted %d file(s)", removed);
    send_ok(session->sock, msg);
}

// Handle LIST_SCREENSHOTS - Scan PS5 screenshot directories
// Structure: /user/av_contents/photo/NPXS40087/{TITLE_ID}/*.jpg|png
// Format: full_path|filename|size|mtime_unix\n...
void handle_list_screenshots(client_session_t *session) {
    const int BUF_SIZE = 262144;  // 256 KB for many screenshots
    char *response = malloc(BUF_SIZE);
    if (!response) {
        send_error(session->sock, "Out of memory");
        return;
    }
    response[0] = '\0';
    int off = 0;
    int count = 0;
    
    // Primary PS5 screenshot locations.
    // NOTE: The *visible* JPEG thumbnails live under /thumbnails/photo/…/.jpg.jpeg
    // The encrypted originals (.dat + .meta) live under /photo/…/ but aren't
    // directly viewable, so we list thumbnails instead.
    const char *roots[] = {
        "/user/av_contents/thumbnails/photo",  // visible JPEG thumbnails (primary)
        "/user/av_contents/thumbnails/video",  // video thumbnails
        "/user/av_contents/photo",             // fallback: any direct .jpg/.png originals
        "/user/av_contents/video",
        "/user/av_contents/sdr",
        "/user/av_contents/extra",
    };
    
    for (size_t i = 0; i < sizeof(roots)/sizeof(roots[0]); i++) {
        count += scan_images_recursive(roots[i], 0, response, &off, BUF_SIZE);
    }
    
    // Fallback: legacy user home paths
    if (count == 0) {
        DIR *home = opendir("/user/home");
        if (home) {
            struct dirent *user_ent;
            while ((user_ent = readdir(home))) {
                if (!strcmp(user_ent->d_name, ".") || !strcmp(user_ent->d_name, "..")) continue;
                
                const char *subdirs[] = { "screenshot", "images/screenshot", "shared" };
                for (size_t si = 0; si < sizeof(subdirs)/sizeof(subdirs[0]); si++) {
                    char ss_path[PATH_MAX];
                    snprintf(ss_path, sizeof(ss_path), "/user/home/%s/%s",
                             user_ent->d_name, subdirs[si]);
                    count += scan_images_recursive(ss_path, 0, response, &off, BUF_SIZE);
                }
            }
            closedir(home);
        }
    }
    
    if (count == 0) {
        strcpy(response, "NO_SCREENSHOTS\n");
        off = strlen(response);
    }
    
    send_response(session->sock, RESP_DATA, response, off);
    free(response);
}

// Handle UNMOUNT_GAME - Unmount a specific game by title ID
void handle_unmount_game(client_session_t *session, const char *title_id) {
    if (!title_id || strlen(title_id) == 0) {
        send_error(session->sock, "No title ID provided");
        return;
    }
    
    // Validate title ID format
    if ((strncmp(title_id, "CUSA", 4) != 0 && 
         strncmp(title_id, "PPSA", 4) != 0) || 
        strlen(title_id) != 9) {
        send_error(session->sock, "Invalid title ID format");
        return;
    }
    
    // Initialize app install utility for unregistration
    sceAppInstUtilInitialize();
    
    // Unregister the game from PS5 system (removes from home screen)
    sceAppInstUtilAppUnInstall(title_id);
    
    // Wait briefly for system to process the unregistration
    usleep(200000); // 200ms
    
    char system_ex_app[PATH_MAX];
    snprintf(system_ex_app, sizeof(system_ex_app), "/system_ex/app/%s", title_id);
    
    // Unmount nullfs if mounted
    if (is_mounted(system_ex_app)) {
        if (unmount(system_ex_app, 0) != 0) {
            unmount(system_ex_app, MNT_FORCE);
        }
    }
    
    // Wait for unmount to complete
    usleep(100000); // 100ms
    
    // Clean up directories
    char user_app_dir[PATH_MAX];
    snprintf(user_app_dir, sizeof(user_app_dir), "/user/app/%s", title_id);
    rmdir_recursive(user_app_dir);
    
    char appmeta_dir[PATH_MAX];
    snprintf(appmeta_dir, sizeof(appmeta_dir), "/user/appmeta/%s", title_id);
    rmdir_recursive(appmeta_dir);
    
    char msg[128];
    snprintf(msg, sizeof(msg), "Unmounted and unregistered %s", title_id);
    send_ok(session->sock, msg);
}

// Helper: read a sysctl string value
static int sysctl_get_string(const char *name, char *buf, size_t buflen) {
    size_t len = buflen;
    if (sysctlbyname(name, buf, &len, NULL, 0) == 0) {
        buf[len < buflen ? len : buflen - 1] = '\0';
        return 0;
    }
    return -1;
}

// Helper: read a sysctl integer value
static int sysctl_get_int(const char *name, int *val) {
    size_t len = sizeof(int);
    return sysctlbyname(name, val, &len, NULL, 0);
}

// Helper: read a sysctl uint64 value
static int sysctl_get_uint64(const char *name, uint64_t *val) {
    size_t len = sizeof(uint64_t);
    return sysctlbyname(name, val, &len, NULL, 0);
}

// Cache static hardware info (never changes). We read it once at first call
// and serve the cached copy forever after — this avoids hitting the kernel
// APIs on every refresh.
static pthread_mutex_t g_hwinfo_lock = PTHREAD_MUTEX_INITIALIZER;
static char g_hwinfo_response[4096];
static int  g_hwinfo_len = 0;
static int  g_hwinfo_valid = 0;

// Handle GET_HW_INFO - static info served from one-shot cache
void handle_get_hw_info(client_session_t *session) {
    pthread_mutex_lock(&g_hwinfo_lock);
    
    if (!g_hwinfo_valid) {
        char model_name[1024] = {0};
        char serial[1024] = {0};
        
        // One-time reads of static kernel hw info
        if (sceKernelGetHwModelName(model_name) != 0 || model_name[0] == '\0') {
            strcpy(model_name, "PlayStation 5");
        }
        if (sceKernelGetHwSerialNumber(serial) != 0 || serial[0] == '\0') {
            strcpy(serial, "N/A");
        }
        
        char hw_machine[256] = {0};
        char ostype[64] = {0};
        char osrelease[64] = {0};
        sysctl_get_string("hw.machine", hw_machine, sizeof(hw_machine));
        sysctl_get_string("kern.ostype", ostype, sizeof(ostype));
        sysctl_get_string("kern.osrelease", osrelease, sizeof(osrelease));
        
        int ncpu = 0;
        sysctl_get_int("hw.ncpu", &ncpu);
        
        // Physical RAM — try PS5-specific API first, then sysctl chain, then hardcoded.
        uint64_t physmem = (uint64_t)sceKernelGetDirectMemorySize();
        if (physmem == 0) sysctl_get_uint64("hw.physmem",  &physmem);
        if (physmem == 0) sysctl_get_uint64("hw.realmem",  &physmem);
        if (physmem == 0) sysctl_get_uint64("hw.usermem",  &physmem);
        if (physmem == 0) {
            int pagesize = 0;
            uint64_t page_count = 0;
            sysctl_get_int("hw.pagesize", &pagesize);
            if (sysctl_get_uint64("vm.stats.vm.v_page_count", &page_count) == 0 &&
                pagesize > 0 && page_count > 0) {
                physmem = page_count * (uint64_t)pagesize;
            }
        }
        // Final fallback: every PS5 has 16 GB GDDR6 (accessible portion ~13 GB)
        if (physmem == 0) {
            physmem = 16ULL * 1024 * 1024 * 1024;
        }
        
        g_hwinfo_len = snprintf(g_hwinfo_response, sizeof(g_hwinfo_response),
            "model=%s\n"
            "serial=%s\n"
            "has_wlan_bt=1\n"
            "has_optical_out=0\n"
            "hw_model=%s\n"
            "hw_machine=%s\n"
            "os=%s %s\n"
            "ncpu=%d\n"
            "physmem=%llu\n",
            model_name, serial, model_name, hw_machine,
            ostype, osrelease, ncpu, (unsigned long long)physmem);
        g_hwinfo_valid = 1;
    }
    
    // Copy out under lock
    char out[4096];
    int out_len = g_hwinfo_len;
    if (out_len > (int)sizeof(out)) out_len = (int)sizeof(out);
    memcpy(out, g_hwinfo_response, out_len);
    pthread_mutex_unlock(&g_hwinfo_lock);
    
    send_response(session->sock, RESP_DATA, out, out_len);
}

// Serialize sensor reads across concurrent client sessions to avoid
// hitting non-reentrant Sony kernel APIs from multiple threads.
static pthread_mutex_t g_sensor_lock = PTHREAD_MUTEX_INITIALIZER;

// Small short-lived cache (1 second) so rapid-fire polls don't hammer the APIs.
static struct {
    time_t last_read;
    int cpu_temp;
    int soc_temp;
    long cpu_freq_mhz;
    uint32_t power_mw;
    int valid;
} g_sensor_cache = {0};

// Handle GET_TEMPS - Uses verified Sony kernel APIs (new SDK 2026)
void handle_get_temps(client_session_t *session) {
    char response[1024];
    int off = 0;
    
    int cpu_temp = 0;
    int soc_temp = 0;
    long cpu_freq_mhz = 0;
    uint32_t power_mw = 0;
    
    pthread_mutex_lock(&g_sensor_lock);
    
    time_t now = time(NULL);
    if (g_sensor_cache.valid && (now - g_sensor_cache.last_read) < 1) {
        // Serve from cache - avoids hammering the kernel APIs
        cpu_temp     = g_sensor_cache.cpu_temp;
        soc_temp     = g_sensor_cache.soc_temp;
        cpu_freq_mhz = g_sensor_cache.cpu_freq_mhz;
        power_mw     = g_sensor_cache.power_mw;
    } else {
        // Fresh read. Only the APIs that appear in the official SDK sample.
        // Each call is guarded individually; defaults to 0 on failure.
        int rc;
        
        rc = sceKernelGetCpuTemperature(&cpu_temp);
        if (rc != 0) cpu_temp = 0;
        
        rc = sceKernelGetSocSensorTemperature(0, &soc_temp);
        if (rc != 0) soc_temp = 0;
        
        long cpu_freq_hz = sceKernelGetCpuFrequency();
        cpu_freq_mhz = (cpu_freq_hz > 0) ? cpu_freq_hz / (1000 * 1000) : 0;
        if (cpu_freq_mhz <= 0) {
            uint64_t tsc_freq = 0;
            if (sysctl_get_uint64("machdep.tsc_freq", &tsc_freq) == 0 && tsc_freq > 0) {
                cpu_freq_mhz = (long)(tsc_freq / 1000000ULL);
            }
        }
        
        // Power consumption API — call at most once every 5 seconds.
        // Separate tracker so the 1s sensor cache doesn't force a re-read of this API.
        static time_t last_power_read = 0;
        static uint32_t last_power_mw = 0;
        if ((now - last_power_read) >= 5) {
            uint32_t pw = 0;
            if (sceKernelGetSocPowerConsumption(&pw) == 0) {
                // Reject abnormal readings (> 500 W is impossible for PS5)
                if (pw < 500000) last_power_mw = pw;
            }
            last_power_read = now;
        }
        power_mw = last_power_mw;
        
        // Update cache
        g_sensor_cache.last_read    = now;
        g_sensor_cache.cpu_temp     = cpu_temp;
        g_sensor_cache.soc_temp     = soc_temp;
        g_sensor_cache.cpu_freq_mhz = cpu_freq_mhz;
        g_sensor_cache.power_mw     = power_mw;
        g_sensor_cache.valid        = 1;
    }
    
    pthread_mutex_unlock(&g_sensor_lock);
    
    off += snprintf(response + off, sizeof(response) - off,
        "cpu_temp=%d\n"
        "soc_temp=%d\n"
        "cpu_freq_mhz=%ld\n"
        "soc_clock_mhz=0\n"
        "soc_power_mw=%u\n",
        cpu_temp,
        soc_temp,
        cpu_freq_mhz,
        power_mw);
    
    for (int i = 0; i < 8; i++) {
        off += snprintf(response + off, sizeof(response) - off,
            "cpu_usage_%d=0\n", i);
    }
    
    send_response(session->sock, RESP_DATA, response, off);
}

// Handle GET_POWER_INFO - Get power/uptime and real power consumption
void handle_get_power_info(client_session_t *session) {
    char response[1024];
    int off = 0;
    
    // Get uptime via kern.boottime
    struct timeval boottime;
    size_t bt_len = sizeof(boottime);
    uint64_t uptime_sec = 0;
    int bt_rc = sysctlbyname("kern.boottime", &boottime, &bt_len, NULL, 0);
    
    if (bt_rc == 0 && boottime.tv_sec > 0) {
        struct timeval now;
        gettimeofday(&now, NULL);
        if (now.tv_sec > boottime.tv_sec) {
            uptime_sec = (uint64_t)(now.tv_sec - boottime.tv_sec);
        }
    }
    
    // Fallback: use clock() or time-based estimate
    if (uptime_sec == 0) {
        // Simple fallback using time(NULL) - epoch seconds
        // Not real uptime but at least non-zero
        uptime_sec = 0;  // Leave as 0 if we can't determine
    }
    
    uint64_t hours = uptime_sec / 3600;
    uint64_t minutes = (uptime_sec % 3600) / 60;
    
    // Use uptime only (safe) - no Sony kernel calls
    off += snprintf(response + off, sizeof(response) - off,
        "operating_time_sec=%llu\n"
        "operating_time_hours=%llu\n"
        "operating_time_minutes=%llu\n"
        "boot_count=0\n"
        "power_consumption_mw=0\n",
        (unsigned long long)uptime_sec,
        (unsigned long long)hours,
        (unsigned long long)minutes);
    
    send_response(session->sock, RESP_DATA, response, off);
}

// Handle GET_RUNNING_APPS - Get list of running applications
void handle_get_running_apps(client_session_t *session) {
    char *response = malloc(16384);
    if (!response) {
        send_error(session->sock, "Out of memory");
        return;
    }
    
    int off = 0;
    
    // Scan /mnt/sandbox for running apps
    DIR *d = opendir("/mnt/sandbox");
    if (d) {
        struct dirent *e;
        while ((e = readdir(d)) && off < 15000) {
            if (!strcmp(e->d_name, ".") || !strcmp(e->d_name, ".."))
                continue;
            
            // Extract title ID from sandbox name (format: PPSA12345_000 or CUSA12345_000)
            char title_id[16] = "";
            if ((strncmp(e->d_name, "PPSA", 4) == 0 || strncmp(e->d_name, "CUSA", 4) == 0) &&
                strlen(e->d_name) >= 9) {
                strncpy(title_id, e->d_name, 9);
                title_id[9] = '\0';
            } else {
                continue;
            }
            
            // Format: pid=X|name=Y|title_id=Z|app_id=W
            off += snprintf(response + off, 16384 - off,
                "pid=0|name=%s|title_id=%s|app_id=0\n", e->d_name, title_id);
        }
        closedir(d);
    }
    
    if (off == 0) {
        strcpy(response, "No apps running\n");
        off = strlen(response);
    }
    
    send_response(session->sock, RESP_DATA, response, off);
    free(response);
}

// Handle KILL_APP - Kill a running application
void handle_kill_app(client_session_t *session, const char *title_id) {
    if (!title_id || strlen(title_id) == 0) {
        send_error(session->sock, "No title ID provided");
        return;
    }
    
    // Placeholder - actual app killing requires SceSystemService calls
    char msg[128];
    snprintf(msg, sizeof(msg), "Kill not implemented for %s", title_id);
    send_error(session->sock, msg);
}

// Handle LAUNCH_BROWSER - Open the PS5 browser
void handle_launch_browser(client_session_t *session, const char *url) {
    // Placeholder
    send_error(session->sock, "Browser launch not implemented");
}

// ============================================================================
// SHELL TERMINAL
// ============================================================================

void handle_shell_open(client_session_t *session) {
    if (session->shell_active) {
        send_error(session->sock, "Shell already active");
        return;
    }
    
    // Initialize shell state
    session->shell_active = true;
    strcpy(session->shell_cwd, "/data");
    session->shell_pipe = NULL;
    session->shell_pid = 0;
    
    send_ok(session->sock, "Shell session opened");
}

// Built-in ls command
void builtin_ls(client_session_t *session, const char *path) {
    const char *target = (path && strlen(path) > 0) ? path : session->shell_cwd;
    
    DIR *dir = opendir(target);
    if (!dir) {
        send_error(session->sock, "Cannot open directory");
        return;
    }
    
    struct dirent *entry;
    char output[256];
    
    while ((entry = readdir(dir)) != NULL) {
        snprintf(output, sizeof(output), "%s\n", entry->d_name);
        
        uint8_t resp = RESP_DATA;
        uint32_t data_len = strlen(output);
        send(session->sock, &resp, 1, 0);
        send(session->sock, &data_len, 4, 0);
        send(session->sock, output, data_len, 0);
    }
    
    closedir(dir);
    send_ok(session->sock, "");
}

// Built-in pwd command
void builtin_pwd(client_session_t *session) {
    char output[MAX_PATH + 1];
    snprintf(output, sizeof(output), "%s\n", session->shell_cwd);
    
    uint8_t resp = RESP_DATA;
    uint32_t data_len = strlen(output);
    send(session->sock, &resp, 1, 0);
    send(session->sock, &data_len, 4, 0);
    send(session->sock, output, data_len, 0);
    
    send_ok(session->sock, "");
}

// Resolve ".." and "." components in a path in-place
static void resolve_path(char *path) {
    char *parts[128];
    int depth = 0;
    char tmp[MAX_PATH];
    strncpy(tmp, path, sizeof(tmp) - 1);
    tmp[sizeof(tmp) - 1] = '\0';
    
    char *token = strtok(tmp, "/");
    while (token) {
        if (strcmp(token, "..") == 0) {
            if (depth > 0) depth--;
        } else if (strcmp(token, ".") != 0 && strlen(token) > 0) {
            parts[depth++] = token;
        }
        token = strtok(NULL, "/");
    }
    
    // Rebuild path
    path[0] = '\0';
    for (int i = 0; i < depth; i++) {
        strcat(path, "/");
        strcat(path, parts[i]);
    }
    if (path[0] == '\0') {
        strcpy(path, "/");
    }
}

// Built-in cd command
void builtin_cd(client_session_t *session, const char *path) {
    char new_path[MAX_PATH];
    
    if (!path || strlen(path) == 0 || strcmp(path, "~") == 0) {
        strcpy(new_path, "/data");
    } else if (path[0] == '/') {
        strncpy(new_path, path, sizeof(new_path) - 1);
        new_path[sizeof(new_path) - 1] = '\0';
    } else {
        snprintf(new_path, sizeof(new_path), "%s/%s", session->shell_cwd, path);
    }
    
    // Resolve ".." and "." components
    resolve_path(new_path);
    
    // Check if directory exists
    DIR *dir = opendir(new_path);
    if (!dir) {
        send_error(session->sock, "Directory not found");
        return;
    }
    closedir(dir);
    
    // Update current directory (safe copy with bounds check)
    strncpy(session->shell_cwd, new_path, sizeof(session->shell_cwd) - 1);
    session->shell_cwd[sizeof(session->shell_cwd) - 1] = '\0';
    send_ok(session->sock, "");
}

// Built-in cat command
void builtin_cat(client_session_t *session, const char *path) {
    if (!path || strlen(path) == 0) {
        send_error(session->sock, "Usage: cat <file>");
        return;
    }
    
    char full_path[MAX_PATH];
    if (path[0] == '/') {
        strcpy(full_path, path);
    } else {
        snprintf(full_path, sizeof(full_path), "%s/%s", session->shell_cwd, path);
    }
    
    FILE *fp = fopen(full_path, "r");
    if (!fp) {
        send_error(session->sock, "Cannot open file");
        return;
    }
    
    char buffer[4096];
    size_t total_sent = 0;
    
    while (fgets(buffer, sizeof(buffer), fp) != NULL) {
        size_t len = strlen(buffer);
        if (len > 0) {
            uint8_t resp = RESP_DATA;
            uint32_t data_len = len;
            send(session->sock, &resp, 1, 0);
            send(session->sock, &data_len, 4, 0);
            send(session->sock, buffer, data_len, 0);
            
            total_sent += len;
            if (total_sent > 1024 * 1024) break; // Max 1MB
        }
    }
    
    fclose(fp);
    send_ok(session->sock, "");
}

// Built-in mkdir command
void builtin_mkdir(client_session_t *session, const char *path) {
    if (!path || strlen(path) == 0) {
        send_error(session->sock, "Usage: mkdir <directory>");
        return;
    }
    
    char full_path[MAX_PATH];
    if (path[0] == '/') {
        strcpy(full_path, path);
    } else {
        snprintf(full_path, sizeof(full_path), "%s/%s", session->shell_cwd, path);
    }
    
    if (mkdir(full_path, 0777) == 0) {
        send_ok(session->sock, "Directory created");
    } else {
        send_error(session->sock, "Failed to create directory");
    }
}

// Built-in rm command
void builtin_rm(client_session_t *session, const char *path) {
    if (!path || strlen(path) == 0) {
        send_error(session->sock, "Usage: rm <file>");
        return;
    }
    
    char full_path[MAX_PATH];
    if (path[0] == '/') {
        strcpy(full_path, path);
    } else {
        snprintf(full_path, sizeof(full_path), "%s/%s", session->shell_cwd, path);
    }
    
    if (unlink(full_path) == 0) {
        send_ok(session->sock, "File deleted");
    } else {
        send_error(session->sock, "Failed to delete file");
    }
}

// Built-in rmdir command
void builtin_rmdir(client_session_t *session, const char *path) {
    if (!path || strlen(path) == 0) {
        send_error(session->sock, "Usage: rmdir <directory>");
        return;
    }
    
    char full_path[MAX_PATH];
    if (path[0] == '/') {
        strcpy(full_path, path);
    } else {
        snprintf(full_path, sizeof(full_path), "%s/%s", session->shell_cwd, path);
    }
    
    if (rmdir(full_path) == 0) {
        send_ok(session->sock, "Directory deleted");
    } else {
        send_error(session->sock, "Failed to delete directory");
    }
}

// Built-in touch command
void builtin_touch(client_session_t *session, const char *path) {
    if (!path || strlen(path) == 0) {
        send_error(session->sock, "Usage: touch <file>");
        return;
    }
    
    char full_path[MAX_PATH];
    if (path[0] == '/') {
        strcpy(full_path, path);
    } else {
        snprintf(full_path, sizeof(full_path), "%s/%s", session->shell_cwd, path);
    }
    
    FILE *fp = fopen(full_path, "a");
    if (fp) {
        fclose(fp);
        send_ok(session->sock, "File created/updated");
    } else {
        send_error(session->sock, "Failed to create file");
    }
}

// Built-in echo command
void builtin_echo(client_session_t *session, const char *text) {
    if (!text) text = "";
    
    char output[MAX_PATH + 2];
    snprintf(output, sizeof(output), "%s\n", text);
    
    uint8_t resp = RESP_DATA;
    uint32_t data_len = strlen(output);
    send(session->sock, &resp, 1, 0);
    send(session->sock, &data_len, 4, 0);
    send(session->sock, output, data_len, 0);
    send_ok(session->sock, "");
}

// Built-in cp command
void builtin_cp(client_session_t *session, const char *args) {
    if (!args || strlen(args) == 0) {
        send_error(session->sock, "Usage: cp <source> <destination>");
        return;
    }
    
    char args_copy[MAX_PATH * 2];
    strncpy(args_copy, args, sizeof(args_copy) - 1);
    
    char *src = strtok(args_copy, " \t");
    char *dst = strtok(NULL, " \t\n");
    
    if (!src || !dst) {
        send_error(session->sock, "Usage: cp <source> <destination>");
        return;
    }
    
    char src_path[MAX_PATH], dst_path[MAX_PATH];
    if (src[0] == '/') strcpy(src_path, src);
    else snprintf(src_path, sizeof(src_path), "%s/%s", session->shell_cwd, src);
    
    if (dst[0] == '/') strcpy(dst_path, dst);
    else snprintf(dst_path, sizeof(dst_path), "%s/%s", session->shell_cwd, dst);
    
    FILE *src_fp = fopen(src_path, "rb");
    if (!src_fp) {
        send_error(session->sock, "Cannot open source file");
        return;
    }
    
    FILE *dst_fp = fopen(dst_path, "wb");
    if (!dst_fp) {
        fclose(src_fp);
        send_error(session->sock, "Cannot create destination file");
        return;
    }
    
    char buffer[8192];
    size_t bytes;
    while ((bytes = fread(buffer, 1, sizeof(buffer), src_fp)) > 0) {
        fwrite(buffer, 1, bytes, dst_fp);
    }
    
    fclose(src_fp);
    fclose(dst_fp);
    send_ok(session->sock, "File copied");
}

// Built-in mv command
void builtin_mv(client_session_t *session, const char *args) {
    if (!args || strlen(args) == 0) {
        send_error(session->sock, "Usage: mv <source> <destination>");
        return;
    }
    
    char args_copy[MAX_PATH * 2];
    strncpy(args_copy, args, sizeof(args_copy) - 1);
    
    char *src = strtok(args_copy, " \t");
    char *dst = strtok(NULL, " \t\n");
    
    if (!src || !dst) {
        send_error(session->sock, "Usage: mv <source> <destination>");
        return;
    }
    
    char src_path[MAX_PATH], dst_path[MAX_PATH];
    if (src[0] == '/') strcpy(src_path, src);
    else snprintf(src_path, sizeof(src_path), "%s/%s", session->shell_cwd, src);
    
    if (dst[0] == '/') strcpy(dst_path, dst);
    else snprintf(dst_path, sizeof(dst_path), "%s/%s", session->shell_cwd, dst);
    
    if (rename(src_path, dst_path) == 0) {
        send_ok(session->sock, "File moved/renamed");
    } else {
        send_error(session->sock, "Failed to move file");
    }
}

// Built-in stat command
void builtin_stat(client_session_t *session, const char *path) {
    if (!path || strlen(path) == 0) {
        send_error(session->sock, "Usage: stat <file>");
        return;
    }
    
    char full_path[MAX_PATH];
    if (path[0] == '/') {
        strcpy(full_path, path);
    } else {
        snprintf(full_path, sizeof(full_path), "%s/%s", session->shell_cwd, path);
    }
    
    struct stat st;
    if (stat(full_path, &st) != 0) {
        send_error(session->sock, "Cannot stat file");
        return;
    }
    
    char output[512];
    snprintf(output, sizeof(output),
        "File: %s\n"
        "Size: %lld bytes\n"
        "Type: %s\n"
        "Permissions: %o\n",
        path,
        (long long)st.st_size,
        S_ISDIR(st.st_mode) ? "Directory" : S_ISREG(st.st_mode) ? "Regular file" : "Other",
        st.st_mode & 0777);
    
    uint8_t resp = RESP_DATA;
    uint32_t data_len = strlen(output);
    send(session->sock, &resp, 1, 0);
    send(session->sock, &data_len, 4, 0);
    send(session->sock, output, data_len, 0);
    send_ok(session->sock, "");
}

// Built-in chmod command
void builtin_chmod(client_session_t *session, const char *args) {
    if (!args || strlen(args) == 0) {
        send_error(session->sock, "Usage: chmod <mode> <file>");
        return;
    }
    
    char args_copy[MAX_PATH];
    strncpy(args_copy, args, sizeof(args_copy) - 1);
    
    char *mode_str = strtok(args_copy, " \t");
    char *path = strtok(NULL, " \t\n");
    
    if (!mode_str || !path) {
        send_error(session->sock, "Usage: chmod <mode> <file>");
        return;
    }
    
    int mode = strtol(mode_str, NULL, 8);
    
    char full_path[MAX_PATH];
    if (path[0] == '/') {
        strcpy(full_path, path);
    } else {
        snprintf(full_path, sizeof(full_path), "%s/%s", session->shell_cwd, path);
    }
    
    if (chmod(full_path, mode) == 0) {
        send_ok(session->sock, "Permissions changed");
    } else {
        send_error(session->sock, "Failed to change permissions");
    }
}

// Handle SHELL_EXEC - Execute command and stream output
void handle_shell_exec(client_session_t *session, const char *command) {
    if (!session->shell_active) {
        send_error(session->sock, "Shell not active");
        return;
    }
    
    if (!command || strlen(command) == 0) {
        send_error(session->sock, "Empty command");
        return;
    }
    
    // Parse command and arguments
    char cmd_copy[MAX_PATH];
    strncpy(cmd_copy, command, sizeof(cmd_copy) - 1);
    cmd_copy[sizeof(cmd_copy) - 1] = '\0';
    
    char *cmd = strtok(cmd_copy, " \t\n");
    char *arg = strtok(NULL, "\n");
    
    if (!cmd) {
        send_error(session->sock, "Empty command");
        return;
    }
    
    // Handle built-in commands
    if (strcmp(cmd, "ls") == 0) {
        builtin_ls(session, arg);
    } else if (strcmp(cmd, "pwd") == 0) {
        builtin_pwd(session);
    } else if (strcmp(cmd, "cd") == 0) {
        builtin_cd(session, arg);
    } else if (strcmp(cmd, "cat") == 0) {
        builtin_cat(session, arg);
    } else if (strcmp(cmd, "mkdir") == 0) {
        builtin_mkdir(session, arg);
    } else if (strcmp(cmd, "rm") == 0) {
        builtin_rm(session, arg);
    } else if (strcmp(cmd, "rmdir") == 0) {
        builtin_rmdir(session, arg);
    } else if (strcmp(cmd, "touch") == 0) {
        builtin_touch(session, arg);
    } else if (strcmp(cmd, "echo") == 0) {
        builtin_echo(session, arg);
    } else if (strcmp(cmd, "cp") == 0) {
        builtin_cp(session, arg);
    } else if (strcmp(cmd, "mv") == 0) {
        builtin_mv(session, arg);
    } else if (strcmp(cmd, "stat") == 0) {
        builtin_stat(session, arg);
    } else if (strcmp(cmd, "chmod") == 0) {
        builtin_chmod(session, arg);
    } else if (strcmp(cmd, "help") == 0) {
        const char *help_text = 
            "PS5 Shell Terminal - Available Commands:\n"
            "\n"
            "FILE OPERATIONS:\n"
            "  ls [path]         - List directory contents\n"
            "  cat <file>        - Display file contents\n"
            "  touch <file>      - Create empty file\n"
            "  rm <file>         - Delete file\n"
            "  cp <src> <dst>    - Copy file\n"
            "  mv <src> <dst>    - Move/rename file\n"
            "  stat <file>       - Show file information\n"
            "  chmod <mode> <f>  - Change file permissions\n"
            "\n"
            "DIRECTORY OPERATIONS:\n"
            "  pwd               - Print working directory\n"
            "  cd [path]         - Change directory\n"
            "  mkdir <dir>       - Create directory\n"
            "  rmdir <dir>       - Delete empty directory\n"
            "\n"
            "UTILITIES:\n"
            "  echo <text>       - Print text\n"
            "  help              - Show this help\n"
            "\n"
            "TIPS:\n"
            "  - Use absolute paths (/data/file) or relative (file)\n"
            "  - Press UP/DOWN arrows for command history\n"
            "  - Type 'cd' or 'cd ~' to go to /data\n";
        
        uint8_t resp = RESP_DATA;
        uint32_t data_len = strlen(help_text);
        send(session->sock, &resp, 1, 0);
        send(session->sock, &data_len, 4, 0);
        send(session->sock, help_text, data_len, 0);
        send_ok(session->sock, "");
    } else {
        send_error(session->sock, "Command not found. Type 'help' for available commands.");
    }
}

// Handle SHELL_INTERRUPT - Not implemented (would need fork/exec for proper signal handling)
void handle_shell_interrupt(client_session_t *session) {
    send_error(session->sock, "Interrupt not supported in this implementation");
}

// Handle SHELL_CLOSE - Close shell session
void handle_shell_close(client_session_t *session) {
    if (!session->shell_active) {
        send_error(session->sock, "Shell not active");
        return;
    }
    
    session->shell_active = false;
    session->shell_pipe = NULL;
    session->shell_pid = 0;
    
    send_ok(session->sock, "Shell session closed");
}

// Handle client
void *client_thread(void *arg) {
    client_session_t *session = (client_session_t *)arg;
    uint8_t *buffer = malloc(BUFFER_SIZE);
    
    if (!buffer) {
        close(session->sock);
        free(session);
        return NULL;
    }
    
    // Initialize upload_fd to -1 (not open)
    session->upload_fd = -1;
    
    // No socket timeout - connection stays open indefinitely until client disconnects
    
    while (1) {
        // Read command header (5 bytes: 1 cmd + 4 data_len)
        uint8_t header[5];
        ssize_t n = recv(session->sock, header, 5, MSG_WAITALL);
        if (n != 5) {
            break;
        }
        
        uint8_t cmd = header[0];
        uint32_t data_len;
        memcpy(&data_len, header + 1, 4);
        
        // Read data if present
        uint8_t *data = NULL;
        if (data_len > 0) {
            if (data_len > BUFFER_SIZE) {
                send_error(session->sock, "Data too large");
                break;
            }
            data = buffer;
            ssize_t received = 0;
            while (received < data_len) {
                n = recv(session->sock, data + received, data_len - received, 0);
                if (n <= 0) {
                    break;
                }
                received += n;
            }
            if (received != data_len) {
                break;
            }
        }
        
        // Handle command
        switch (cmd) {
            case CMD_PING:
                handle_ping(session);
                break;
            case CMD_LIST_STORAGE:
                handle_list_storage(session);
                break;
            case CMD_LIST_DIR:
                if (data) {
                    handle_list_dir(session, (const char *)data);
                }
                break;
            case CMD_CREATE_DIR:
                if (data) {
                    handle_create_dir(session, (const char *)data);
                }
                break;
            case CMD_DELETE_FILE:
                if (data) {
                    handle_delete_file(session, (const char *)data);
                }
                break;
            case CMD_DELETE_DIR:
                if (data) {
                    handle_delete_dir(session, (const char *)data);
                }
                break;
            case CMD_RENAME:
                if (data) {
                    handle_rename(session, data, data_len);
                }
                break;
            case CMD_COPY_FILE:
                if (data) {
                    handle_copy_file(session, data, data_len);
                }
                break;
            case CMD_MOVE_FILE:
                if (data) {
                    handle_move_file(session, data, data_len);
                }
                break;
            case CMD_START_UPLOAD:
                if (data) {
                    handle_start_upload(session, data, data_len);
                }
                break;
            case CMD_UPLOAD_CHUNK:
                if (data) {
                    handle_upload_chunk(session, data, data_len);
                }
                break;
            case CMD_END_UPLOAD:
                handle_end_upload(session);
                break;
            case CMD_DOWNLOAD_FILE:
                if (data) {
                    handle_download_file(session, (const char *)data);
                }
                break;
            case CMD_MOUNT_GAMES:
                handle_mount_games(session);
                break;
            case CMD_GET_HW_INFO:
                handle_get_hw_info(session);
                break;
            case CMD_GET_TEMPS:
                handle_get_temps(session);
                break;
            case CMD_GET_GAME_LIST:
                handle_get_game_list(session);
                break;
            case CMD_UNMOUNT_GAME:
                if (data) {
                    handle_unmount_game(session, (const char *)data);
                }
                break;
            case CMD_GET_GAME_ICON:
                if (data) {
                    handle_get_game_icon(session, (const char *)data);
                } else {
                    send_error(session->sock, "No title ID provided");
                }
                break;
            case CMD_GET_GAME_DETAILS:
                if (data) {
                    handle_get_game_details(session, (const char *)data);
                } else {
                    send_error(session->sock, "No title ID provided");
                }
                break;
            case CMD_GET_GAME_PIC:
                if (data) {
                    handle_get_game_pic(session, (const char *)data);
                } else {
                    send_error(session->sock, "No request data provided");
                }
                break;
            case CMD_LIST_SAVES:
                handle_list_saves(session);
                break;
            case CMD_LAUNCH_GAME:
                if (data) {
                    handle_launch_game(session, (const char *)data);
                } else {
                    send_error(session->sock, "No title ID provided");
                }
                break;
            case CMD_LIST_SCREENSHOTS:
                handle_list_screenshots(session);
                break;
            case CMD_DELETE_SCREENSHOT:
                if (data) {
                    handle_delete_screenshot(session, (const char *)data);
                } else {
                    send_error(session->sock, "No path provided");
                }
                break;
            case CMD_GET_POWER_INFO:
                handle_get_power_info(session);
                break;
            case CMD_GET_RUNNING_APPS:
                handle_get_running_apps(session);
                break;
            case CMD_KILL_APP:
                if (data) {
                    handle_kill_app(session, (const char *)data);
                }
                break;
            case CMD_LAUNCH_BROWSER:
                if (data) {
                    handle_launch_browser(session, (const char *)data);
                }
                break;
            case CMD_SHELL_OPEN:
                handle_shell_open(session);
                break;
            case CMD_SHELL_EXEC:
                if (data) {
                    handle_shell_exec(session, (const char *)data);
                }
                break;
            case CMD_SHELL_INTERRUPT:
                handle_shell_interrupt(session);
                break;
            case CMD_SHELL_CLOSE:
                handle_shell_close(session);
                break;
            case CMD_INDEX_START:
                if (data) {
                    handle_index_start(session, (const char *)data);
                }
                break;
            case CMD_INDEX_STATUS:
                handle_index_status(session);
                break;
            case CMD_SEARCH_INDEX:
                if (data) {
                    handle_search_index(session, (const char *)data);
                }
                break;
            case CMD_INDEX_CANCEL:
                send_error(session->sock, "Index cancel not implemented yet");
                break;
            case CMD_SHUTDOWN:
                send_ok(session->sock, "Shutting down");
                free(buffer);
                close(session->sock);
                if (session->upload_fd >= 0) {
                    close(session->upload_fd);
                    if (session->file_mutex) {
                        release_file_mutex(session->upload_path);
                    }
                }
                free(session);
                exit(0);
            default:
                send_error(session->sock, "Unknown command");
                break;
        }
    }
    
    free(buffer);
    close(session->sock);
    if (session->upload_fd >= 0) {
        close(session->upload_fd);
        if (session->file_mutex) {
            release_file_mutex(session->upload_path);
        }
    }
    free(session);
    return NULL;
}

int main() {
    // Initialize worker threads for async disk I/O
    init_workers();
    
    // Initialize index system
    pthread_mutex_init(&g_index.mutex, NULL);
    g_index.entries = NULL;
    g_index.total_files = 0;
    g_index.total_dirs = 0;
    g_index.indexing = false;
    g_index.ready = false;
    
    int server_sock;
    struct sockaddr_in server_addr;
    
    server_sock = socket(AF_INET, SOCK_STREAM, 0);
    if (server_sock < 0) {
        return 1;
    }
    
    int opt = 1;
    setsockopt(server_sock, SOL_SOCKET, SO_REUSEADDR, &opt, sizeof(opt));
    
    // Prevent SIGPIPE
    int no_sigpipe = 1;
    setsockopt(server_sock, SOL_SOCKET, SO_NOSIGPIPE, &no_sigpipe, sizeof(no_sigpipe));
    
    // 16MB buffers for maximum throughput
    int buf_size = 16 * 1024 * 1024;
    setsockopt(server_sock, SOL_SOCKET, SO_RCVBUF, &buf_size, sizeof(buf_size));
    setsockopt(server_sock, SOL_SOCKET, SO_SNDBUF, &buf_size, sizeof(buf_size));
    
    memset(&server_addr, 0, sizeof(server_addr));
    server_addr.sin_family = AF_INET;
    server_addr.sin_addr.s_addr = INADDR_ANY;
    server_addr.sin_port = htons(SERVER_PORT);
    
    if (bind(server_sock, (struct sockaddr*)&server_addr, sizeof(server_addr)) < 0) {
        close(server_sock);
        return 1;
    }
    
    // Increase backlog to handle multiple parallel connections (up to 128)
    if (listen(server_sock, 128) < 0) {
        close(server_sock);
        return 1;
    }
    
    // Get IP address
    char ip_str[INET_ADDRSTRLEN] = "0.0.0.0";
    struct ifaddrs *ifaddr, *ifa;
    if (getifaddrs(&ifaddr) == 0) {
        for (ifa = ifaddr; ifa != NULL; ifa = ifa->ifa_next) {
            if (ifa->ifa_addr == NULL) continue;
            if (ifa->ifa_addr->sa_family == AF_INET) {
                struct sockaddr_in *addr = (struct sockaddr_in *)ifa->ifa_addr;
                inet_ntop(AF_INET, &addr->sin_addr, ip_str, INET_ADDRSTRLEN);
                if (strcmp(ip_str, "127.0.0.1") != 0) {
                    break;
                }
            }
        }
        freeifaddrs(ifaddr);
    }
    
    char msg[128];
    snprintf(msg, sizeof(msg), "PS5 Upload Server v3.0: %s:%d - By Manos", ip_str, SERVER_PORT);
    send_notification(msg);
    
    while (1) {
        struct sockaddr_in client_addr;
        socklen_t client_len = sizeof(client_addr);
        
        int client_sock = accept(server_sock, (struct sockaddr*)&client_addr, &client_len);
        if (client_sock < 0) {
            continue;
        }
        
        // Aggressive TCP socket options for sustained high speed
        setsockopt(client_sock, SOL_SOCKET, SO_NOSIGPIPE, &no_sigpipe, sizeof(no_sigpipe));
        
        // Increase buffers to 16MB for maximum throughput
        int large_buf = 16 * 1024 * 1024;
        setsockopt(client_sock, SOL_SOCKET, SO_RCVBUF, &large_buf, sizeof(large_buf));
        setsockopt(client_sock, SOL_SOCKET, SO_SNDBUF, &large_buf, sizeof(large_buf));
        
        // TCP optimizations - TCP_NODELAY for immediate send
        int nodelay = 1;
        setsockopt(client_sock, IPPROTO_TCP, TCP_NODELAY, &nodelay, sizeof(nodelay));
        
        // TCP_MAXSEG to prevent fragmentation and maintain high speed
        int maxseg = 1460; // Standard Ethernet MSS
        setsockopt(client_sock, IPPROTO_TCP, TCP_MAXSEG, &maxseg, sizeof(maxseg));
        
        // NOTE: Removed TCP_NOPUSH - it was causing buffering delays!
        
        // CRITICAL: Unlimited timeout for files of ANY size
        // Keepalive will detect and close dead connections (~25s)
        // This allows 50GB+ files to upload without timeout issues
        struct timeval tv;
        tv.tv_sec = 0;  // 0 = unlimited timeout
        tv.tv_usec = 0;
        setsockopt(client_sock, SOL_SOCKET, SO_RCVTIMEO, &tv, sizeof(tv));
        setsockopt(client_sock, SOL_SOCKET, SO_SNDTIMEO, &tv, sizeof(tv));
        
        // CRITICAL: Aggressive keepalive to prevent connection drops on large files
        int keepalive = 1;
        setsockopt(client_sock, SOL_SOCKET, SO_KEEPALIVE, &keepalive, sizeof(keepalive));
        
        // Set keepalive parameters (FreeBSD/PS5)
        int keepidle = 10;   // Start keepalive after 10 seconds of idle
        int keepintvl = 5;   // Send keepalive every 5 seconds
        int keepcnt = 3;     // Drop connection after 3 failed keepalives
        setsockopt(client_sock, IPPROTO_TCP, TCP_KEEPIDLE, &keepidle, sizeof(keepidle));
        setsockopt(client_sock, IPPROTO_TCP, TCP_KEEPINTVL, &keepintvl, sizeof(keepintvl));
        setsockopt(client_sock, IPPROTO_TCP, TCP_KEEPCNT, &keepcnt, sizeof(keepcnt));
        
        client_session_t *session = malloc(sizeof(client_session_t));
        if (!session) {
            close(client_sock);
            continue;
        }
        
        memset(session, 0, sizeof(client_session_t));
        session->sock = client_sock;
        
        pthread_t thread;
        pthread_attr_t attr;
        pthread_attr_init(&attr);
        pthread_attr_setdetachstate(&attr, PTHREAD_CREATE_DETACHED);
        
        if (pthread_create(&thread, &attr, client_thread, session) != 0) {
            close(client_sock);
            free(session);
        }
        
        pthread_attr_destroy(&attr);
    }
    
    close(server_sock);
    return 0;
}
