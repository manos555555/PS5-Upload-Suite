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
// #include <sys/statvfs.h>  // REMOVED - No longer needed
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

#define SERVER_PORT 9113
#define BUFFER_SIZE (8 * 1024 * 1024)  // 8MB for maximum throughput
#define MAX_PATH 2048
#define DISK_WORKER_COUNT 4
#define QUEUE_MAX_SIZE 32

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
// #define CMD_LIST_STORAGE 0x02  // REMOVED - No longer show disk space
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

void send_notification(const char *msg) {
    notify_request_t req;
    memset(&req, 0, sizeof(req));
    strncpy(req.message, msg, sizeof(req.message) - 1);
    sceKernelSendNotificationRequest(0, &req, sizeof(req), 0);
}

typedef struct {
    int sock;
    int upload_fd;  // File descriptor for direct write (faster than FILE*)
    pthread_mutex_t *file_mutex;  // Per-file mutex
    char upload_path[MAX_PATH];
    uint64_t upload_size;
    uint64_t upload_received;
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
        send(sock, header, 5, 0);
        if (data && data_len > 0) {
            send(sock, data, data_len, 0);
        }
        return;
    }
    
    combined[0] = response;
    memcpy(combined + 1, &data_len, 4);
    if (data && data_len > 0) {
        memcpy(combined + 5, data, data_len);
    }
    
    send(sock, combined, total_len, 0);
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

// Global deletion progress counter
static int g_delete_count = 0;
static int g_total_files = 0;
static time_t g_last_notify = 0;
static int g_client_sock = 0;

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

// Send progress message to client
void send_progress_message(const char *msg) {
    if (g_client_sock > 0) {
        uint8_t header[5];
        header[0] = RESP_PROGRESS;
        uint32_t len = strlen(msg) + 1;
        memcpy(header + 1, &len, 4);
        send(g_client_sock, header, 5, 0);
        send(g_client_sock, msg, len, 0);
    }
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

// REMOVED: handle_list_storage() - No longer show disk space to avoid privacy concerns

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
        if (g_client_sock > 0) {
            uint8_t header[5];
            header[0] = RESP_OK;
            uint32_t len = 0;
            memcpy(header + 1, &len, 4);
            send(g_client_sock, header, 5, 0);
        }
        
        g_client_sock = 0;
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
        if (g_client_sock > 0) {
            uint8_t header[5];
            header[0] = RESP_OK;
            uint32_t len = 0;
            memcpy(header + 1, &len, 4);
            send(g_client_sock, header, 5, 0);
            
            // Force flush and wait for data to be sent
            struct timespec ts;
            ts.tv_sec = 0;
            ts.tv_nsec = 200000000; // 200ms
            nanosleep(&ts, NULL);
        }
    } else {
        char msg[256];
        snprintf(msg, sizeof(msg), "❌ Failed to delete folder (%d files removed)", g_delete_count);
        send_progress_message(msg);
        
        // Send error response
        if (g_client_sock > 0) {
            uint8_t header[5];
            header[0] = RESP_ERROR;
            uint32_t len = 0;
            memcpy(header + 1, &len, 4);
            send(g_client_sock, header, 5, 0);
            
            // Force flush and wait for data to be sent
            struct timespec ts;
            ts.tv_sec = 0;
            ts.tv_nsec = 200000000; // 200ms
            nanosleep(&ts, NULL);
        }
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
    
    // Lock ONLY this file's mutex - other files can write in parallel!
    pthread_mutex_lock(session->file_mutex);
    
    // Direct write syscall for maximum speed (matches download optimization)
    ssize_t written = write(session->upload_fd, data, data_len);
    
    if (written != data_len) {
        pthread_mutex_unlock(session->file_mutex);
        send_error(session->sock, "Write failed");
        close(session->upload_fd);
        session->upload_fd = -1;
        release_file_mutex(session->upload_path);
        session->file_mutex = NULL;
        return;
    }
    
    session->upload_received += written;
    
    pthread_mutex_unlock(session->file_mutex);
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

// Built-in cd command
void builtin_cd(client_session_t *session, const char *path) {
    char new_path[MAX_PATH];
    
    if (!path || strlen(path) == 0 || strcmp(path, "~") == 0) {
        strcpy(new_path, "/data");
    } else if (path[0] == '/') {
        strcpy(new_path, path);
    } else {
        snprintf(new_path, sizeof(new_path), "%s/%s", session->shell_cwd, path);
    }
    
    // Check if directory exists
    DIR *dir = opendir(new_path);
    if (!dir) {
        send_error(session->sock, "Directory not found");
        return;
    }
    closedir(dir);
    
    // Update current directory
    strcpy(session->shell_cwd, new_path);
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
            // case CMD_LIST_STORAGE:  // REMOVED - No longer show disk space
            //     handle_list_storage(session);
            //     break;
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
