#include <freerdp/config.h>
#include <freerdp/client/channels.h>
#include <freerdp/channels/log.h>
#include <winpr/crt.h>
#include <winpr/stream.h>

#include <errno.h>
#include <poll.h>
#include <pthread.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

#ifdef __ANDROID__
#include <jni.h>
#endif

#include "rdptun_main.h"

#define TAG CHANNELS_TAG("rdptun.client")
#define MAX_PACKET 4096
#define RX_BUFFER 65536

typedef struct
{
    GENERIC_DYNVC_PLUGIN baseDynPlugin;
} RDPTUN_PLUGIN;

typedef struct
{
    GENERIC_CHANNEL_CALLBACK base;
    uint8_t rx[RX_BUFFER];
    size_t rx_len;
} RDPTUN_CHANNEL_CALLBACK;

static pthread_mutex_t g_lock = PTHREAD_MUTEX_INITIALIZER;
static IWTSVirtualChannel *g_channel = NULL;
static int g_tun_fd = -1;
static pthread_t g_tx_thread;
static int g_tx_started = 0;
static volatile int g_tx_stop = 0;

static int dvc_write_frame(const uint8_t *packet, size_t len)
{
    uint8_t frame[MAX_PACKET + 2];
    UINT rc = CHANNEL_RC_OK;

    if (len == 0 || len > MAX_PACKET || len > 65535)
        return -1;

    frame[0] = (uint8_t)((len >> 8) & 0xff);
    frame[1] = (uint8_t)(len & 0xff);
    memcpy(frame + 2, packet, len);

    pthread_mutex_lock(&g_lock);
    if (g_channel == NULL)
    {
        pthread_mutex_unlock(&g_lock);
        return -1;
    }
    rc = g_channel->Write(g_channel, (ULONG)(len + 2), frame, NULL);
    pthread_mutex_unlock(&g_lock);

    return rc == CHANNEL_RC_OK ? 0 : -1;
}

static void *tun_to_dvc_thread(void *arg)
{
    (void)arg;
    uint8_t packet[MAX_PACKET];

    while (!g_tx_stop)
    {
        int fd;
        pthread_mutex_lock(&g_lock);
        fd = g_tun_fd;
        pthread_mutex_unlock(&g_lock);

        if (fd < 0)
        {
            usleep(100000);
            continue;
        }

        struct pollfd pfd = { .fd = fd, .events = POLLIN, .revents = 0 };
        int pr = poll(&pfd, 1, 250);
        if (pr < 0)
        {
            if (errno == EINTR)
                continue;
            break;
        }
        if (pr == 0 || !(pfd.revents & POLLIN))
            continue;

        ssize_t n = read(fd, packet, sizeof(packet));
        if (n <= 0)
            continue;

        if ((packet[0] >> 4) != 4)
            continue;

        (void)dvc_write_frame(packet, (size_t)n);
    }
    return NULL;
}

static int start_tx_thread_locked(void)
{
    if (g_tx_started)
        return 0;
    g_tx_stop = 0;
    if (pthread_create(&g_tx_thread, NULL, tun_to_dvc_thread, NULL) != 0)
        return -1;
    g_tx_started = 1;
    return 0;
}

static void rdptun_set_tun_fd_internal(int fd)
{
    int oldfd;
    pthread_mutex_lock(&g_lock);
    oldfd = g_tun_fd;
    g_tun_fd = fd;
    (void)start_tx_thread_locked();
    pthread_mutex_unlock(&g_lock);
    if (oldfd >= 0)
        close(oldfd);
}

static void rdptun_close_tun_internal(void)
{
    int oldfd;
    int join = 0;

    pthread_mutex_lock(&g_lock);
    oldfd = g_tun_fd;
    g_tun_fd = -1;
    g_tx_stop = 1;
    join = g_tx_started;
    pthread_mutex_unlock(&g_lock);

    if (oldfd >= 0)
        close(oldfd);
    if (join)
        pthread_join(g_tx_thread, NULL);

    pthread_mutex_lock(&g_lock);
    g_tx_started = 0;
    pthread_mutex_unlock(&g_lock);
}

static UINT rdptun_on_open(IWTSVirtualChannelCallback *pChannelCallback)
{
    GENERIC_CHANNEL_CALLBACK *cb = (GENERIC_CHANNEL_CALLBACK *)pChannelCallback;
    pthread_mutex_lock(&g_lock);
    g_channel = cb->channel;
    (void)start_tx_thread_locked();
    pthread_mutex_unlock(&g_lock);
    WLog_INFO(TAG, "DVC '%s' opened", RDPTUN_DVC_CHANNEL_NAME);
    return CHANNEL_RC_OK;
}

static UINT rdptun_on_data_received(IWTSVirtualChannelCallback *pChannelCallback, wStream *data)
{
    RDPTUN_CHANNEL_CALLBACK *cb = (RDPTUN_CHANNEL_CALLBACK *)pChannelCallback;
    const uint8_t *src = Stream_ConstPointer(data);
    size_t n = Stream_GetRemainingLength(data);

    if (cb->rx_len + n > sizeof(cb->rx))
    {
        cb->rx_len = 0;
        return ERROR_INVALID_DATA;
    }
    memcpy(cb->rx + cb->rx_len, src, n);
    cb->rx_len += n;

    size_t off = 0;
    while (cb->rx_len - off >= 2)
    {
        size_t plen = ((size_t)cb->rx[off] << 8) | cb->rx[off + 1];
        if (plen == 0 || plen > MAX_PACKET)
        {
            cb->rx_len = 0;
            return ERROR_INVALID_DATA;
        }
        if (cb->rx_len - off < plen + 2)
            break;

        int fd;
        pthread_mutex_lock(&g_lock);
        fd = g_tun_fd;
        if (fd >= 0 && (cb->rx[off + 2] >> 4) == 4)
            (void)write(fd, cb->rx + off + 2, plen);
        pthread_mutex_unlock(&g_lock);

        off += plen + 2;
    }

    if (off > 0)
    {
        memmove(cb->rx, cb->rx + off, cb->rx_len - off);
        cb->rx_len -= off;
    }
    return CHANNEL_RC_OK;
}

static UINT rdptun_on_close(IWTSVirtualChannelCallback *pChannelCallback)
{
    GENERIC_CHANNEL_CALLBACK *cb = (GENERIC_CHANNEL_CALLBACK *)pChannelCallback;
    pthread_mutex_lock(&g_lock);
    if (g_channel == cb->channel)
        g_channel = NULL;
    pthread_mutex_unlock(&g_lock);
    WLog_INFO(TAG, "DVC '%s' closed", RDPTUN_DVC_CHANNEL_NAME);
    free(pChannelCallback);
    return CHANNEL_RC_OK;
}

static void rdptun_terminate(GENERIC_DYNVC_PLUGIN *plugin)
{
    (void)plugin;
    rdptun_close_tun_internal();
}

static const IWTSVirtualChannelCallback rdptun_callbacks = {
    rdptun_on_data_received,
    rdptun_on_open,
    rdptun_on_close,
    NULL
};

FREERDP_ENTRY_POINT(UINT VCAPITYPE rdptun_DVCPluginEntry(IDRDYNVC_ENTRY_POINTS *pEntryPoints))
{
    return freerdp_generic_DVCPluginEntry(pEntryPoints, TAG, RDPTUN_DVC_CHANNEL_NAME,
                                          sizeof(RDPTUN_PLUGIN),
                                          sizeof(RDPTUN_CHANNEL_CALLBACK),
                                          &rdptun_callbacks, NULL, rdptun_terminate);
}

#ifdef __ANDROID__
JNIEXPORT jboolean JNICALL
Java_com_freerdp_rdptun_RdpTunnelNative_attachTunFd(JNIEnv *env, jclass clazz, jint fd)
{
    (void)env;
    (void)clazz;
    if (fd < 0)
        return JNI_FALSE;
    rdptun_set_tun_fd_internal((int)fd);
    return JNI_TRUE;
}

JNIEXPORT void JNICALL
Java_com_freerdp_rdptun_RdpTunnelNative_closeTun(JNIEnv *env, jclass clazz)
{
    (void)env;
    (void)clazz;
    rdptun_close_tun_internal();
}
#endif
