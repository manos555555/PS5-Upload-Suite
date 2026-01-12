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
#include <sys/socket.h>
#include <netinet/in.h>
#include <netinet/tcp.h>
#include <arpa/inet.h>
#include <sys/stat.h>
#include <sys/statvfs.h>
#include <fcntl.h>
#include <dirent.h>
#include <pthread.h>
#include <time.h>
#include <ifaddrs.h>

#define SERVER_PORT 9113
#define BUFFER_SIZE (8 * 1024 * 1024)
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
    FILE *upload_fp;
    pthread_mutex_t *file_mutex;  // Per-file mutex
    char upload_path[MAX_PATH];
    uint64_t upload_size;
    uint64_t upload_received;
} client_session_t;

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

// Recursive directory creation
int mkdir_recursive(const char *path) {
    char tmp[MAX_PATH];
    char *p = NULL;
    size_t len;

    snprintf(tmp, sizeof(tmp), "%s", path);
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

// Handle LIST_STORAGE
void handle_list_storage(client_session_t *session) {
    const char *mounts[] = {"/data", "/mnt/usb0", "/mnt/usb1", "/system", "/system_ex"};
    int mount_count = 0;
    
    // Count available mounts
    for (int i = 0; i < 5; i++) {
        struct stat st;
        if (stat(mounts[i], &st) == 0) {
            mount_count++;
        }
    }
    
    // Build response
    size_t buf_size = 4 + (mount_count * (2 + MAX_PATH + 16));
    uint8_t *buffer = malloc(buf_size);
    if (!buffer) {
        send_error(session->sock, "Memory allocation failed");
        return;
    }
    
    uint8_t *ptr = buffer;
    memcpy(ptr, &mount_count, 4);
    ptr += 4;
    
    for (int i = 0; i < 5; i++) {
        struct statvfs vfs;
        if (statvfs(mounts[i], &vfs) == 0) {
            uint16_t path_len = strlen(mounts[i]);
            uint64_t total = (uint64_t)vfs.f_blocks * vfs.f_frsize;
            uint64_t free = (uint64_t)vfs.f_bfree * vfs.f_frsize;
            
            memcpy(ptr, &path_len, 2);
            ptr += 2;
            memcpy(ptr, mounts[i], path_len);
            ptr += path_len;
            memcpy(ptr, &total, 8);
            ptr += 8;
            memcpy(ptr, &free, 8);
            ptr += 8;
        }
    }
    
    send_response(session->sock, RESP_DATA, buffer, ptr - buffer);
    free(buffer);
}

// Handle LIST_DIR - Optimized version using d_type only (no stat for dirs)
void handle_list_dir(client_session_t *session, const char *path) {
    DIR *dir = opendir(path);
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
        
        // Determine type - use d_type if available, otherwise fall back to stat()
        uint8_t type = 0;
        if (entry->d_type == DT_DIR) {
            type = 1;
        } else if (entry->d_type == DT_UNKNOWN) {
            // d_type not supported on this filesystem - use stat() as fallback
            snprintf(full_path, sizeof(full_path), "%s/%s", path, entry->d_name);
            struct stat st;
            if (stat(full_path, &st) == 0 && S_ISDIR(st.st_mode)) {
                type = 1;
            }
        }
        // else: DT_REG or other = file (type = 0)
        
        uint64_t size = 0;      // Don't need file size for browsing
        uint64_t timestamp = 0; // Don't need timestamp for browsing
        
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
    if (unlink(path) == 0) {
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

// Handle DELETE_DIR - BUG FIX: Async deletion to prevent timeout
void handle_delete_dir(client_session_t *session, const char *path) {
    // Send OK response immediately to prevent timeout
    send_ok(session->sock, "Deleting folder in background...");
    
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
            // Thread creation failed, delete synchronously
            free(data);
            rmdir_recursive(path);
        }
        
        pthread_attr_destroy(&attr);
    } else {
        // Malloc failed, delete synchronously
        rmdir_recursive(path);
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
    
    if (rename(old_path, new_path) == 0) {
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
    
    int src_fd = open(src, O_RDONLY);
    if (src_fd < 0) {
        send_error(session->sock, "Cannot open source file");
        return;
    }
    
    int dst_fd = open(dst, O_WRONLY | O_CREAT | O_TRUNC, 0777);
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
    chmod(dst, 0777);
    
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
    
    if (rename(src, dst) == 0) {
        send_ok(session->sock, "File moved");
    } else {
        send_error(session->sock, "Failed to move file");
    }
}

// Handle START_UPLOAD (with optional chunk offset for parallel upload)
void handle_start_upload(client_session_t *session, const uint8_t *data, uint32_t data_len) {
    if (session->upload_fp) {
        fclose(session->upload_fp);
        session->upload_fp = NULL;
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
    
    uint64_t file_size;
    memcpy(&file_size, data + path_len + 1, 8);
    
    // Check for optional offset (for chunked parallel upload)
    uint64_t chunk_offset = 0;
    if (path_len + 17 <= data_len) {
        memcpy(&chunk_offset, data + path_len + 9, 8);
    }
    
    // Create parent directories
    char parent[MAX_PATH];
    strncpy(parent, path, sizeof(parent) - 1);
    parent[sizeof(parent) - 1] = '\0';
    char *last_slash = strrchr(parent, '/');
    if (last_slash) {
        *last_slash = '\0';
        mkdir_recursive(parent);
    }
    
    // Get per-file mutex for this specific file
    session->file_mutex = get_file_mutex(path);
    if (!session->file_mutex) {
        send_error(session->sock, "Cannot allocate file mutex");
        return;
    }
    
    // CRITICAL: Lock mutex BEFORE opening file to prevent race condition
    // when multiple threads try to create the same file simultaneously
    pthread_mutex_lock(session->file_mutex);
    
    // Open file for writing
    if (chunk_offset > 0) {
        // Chunk mode: open existing file or create if needed
        session->upload_fp = fopen(path, "r+b");
        if (!session->upload_fp) {
            // File doesn't exist, create it with full size
            session->upload_fp = fopen(path, "wb");
            if (session->upload_fp) {
                // Allocate full file size
                if (fseeko(session->upload_fp, file_size - 1, SEEK_SET) == 0) {
                    fputc(0, session->upload_fp);
                    fflush(session->upload_fp);
                }
                fclose(session->upload_fp);
                session->upload_fp = fopen(path, "r+b");
            }
        }
        if (session->upload_fp) {
            // Seek to chunk offset
            fseeko(session->upload_fp, chunk_offset, SEEK_SET);
        }
    } else {
        // Normal mode: create new file
        session->upload_fp = fopen(path, "wb");
    }
    
    pthread_mutex_unlock(session->file_mutex);
    
    if (!session->upload_fp) {
        release_file_mutex(path);
        session->file_mutex = NULL;
        send_error(session->sock, "Cannot create file");
        return;
    }
    
    // Use HUGE buffer (8MB) for maximum write speed
    setvbuf(session->upload_fp, NULL, _IOFBF, BUFFER_SIZE);
    
    strncpy(session->upload_path, path, sizeof(session->upload_path) - 1);
    session->upload_size = file_size;
    session->upload_received = chunk_offset;
    
    // Increase socket receive buffer for this upload session
    int huge_buf = 8 * 1024 * 1024; // 8MB receive buffer
    setsockopt(session->sock, SOL_SOCKET, SO_RCVBUF, &huge_buf, sizeof(huge_buf));
    
    send_response(session->sock, RESP_READY, NULL, 0);
}

// Handle UPLOAD_CHUNK
void handle_upload_chunk(client_session_t *session, const uint8_t *data, uint32_t data_len) {
    if (!session->upload_fp || !session->file_mutex) {
        send_error(session->sock, "No upload in progress");
        return;
    }
    
    // Lock ONLY this file's mutex - other files can write in parallel!
    pthread_mutex_lock(session->file_mutex);
    
    size_t written = fwrite(data, 1, data_len, session->upload_fp);
    
    if (written != data_len) {
        pthread_mutex_unlock(session->file_mutex);
        send_error(session->sock, "Write failed");
        fclose(session->upload_fp);
        session->upload_fp = NULL;
        release_file_mutex(session->upload_path);
        session->file_mutex = NULL;
        return;
    }
    
    session->upload_received += written;
    
    // Flush every 32MB to prevent buffer overflow - MUST be inside mutex!
    if (session->upload_received % (32 * 1024 * 1024) < data_len) {
        fflush(session->upload_fp);
    }
    
    pthread_mutex_unlock(session->file_mutex);
    // No response - zero blocking for maximum speed
}

// Handle END_UPLOAD
void handle_end_upload(client_session_t *session) {
    if (!session->upload_fp) {
        send_error(session->sock, "No upload in progress");
        return;
    }
    
    fflush(session->upload_fp);
    fclose(session->upload_fp);
    session->upload_fp = NULL;
    
    if (session->file_mutex) {
        release_file_mutex(session->upload_path);
        session->file_mutex = NULL;
    }
    
    chmod(session->upload_path, 0777);
    
    send_ok(session->sock, "Upload complete");
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
            case CMD_SHUTDOWN:
                send_ok(session->sock, "Shutting down");
                free(buffer);
                close(session->sock);
                if (session->upload_fp) {
                    fclose(session->upload_fp);
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
    if (session->upload_fp) {
        fclose(session->upload_fp);
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
    
    // 8MB buffers for sustained high throughput
    int buf_size = 8 * 1024 * 1024;
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
        
        // Increase buffers to 8MB for maximum throughput
        int large_buf = 8 * 1024 * 1024;
        setsockopt(client_sock, SOL_SOCKET, SO_RCVBUF, &large_buf, sizeof(large_buf));
        setsockopt(client_sock, SOL_SOCKET, SO_SNDBUF, &large_buf, sizeof(large_buf));
        
        // TCP optimizations - TCP_NODELAY for immediate send
        int nodelay = 1;
        setsockopt(client_sock, IPPROTO_TCP, TCP_NODELAY, &nodelay, sizeof(nodelay));
        
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
