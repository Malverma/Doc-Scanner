/*
 * A fake 32-bit TWAIN data source named "TW-Brother DS-640", for testing the worker under Wine.
 * It follows the TWAIN protocol and generates a known pattern. It says nothing about how Brother's
 * real driver behaves. FAKE_DS_MODE selects the scenario; see run.sh.
 */
#include <stdint.h>

typedef void *HANDLE;
typedef void *HMODULE;
typedef void *FARPROC;
#define WINAPI __stdcall
__declspec(dllimport) HANDLE WINAPI GlobalAlloc(unsigned flags, unsigned long size);
__declspec(dllimport) void *WINAPI GlobalLock(HANDLE h);
__declspec(dllimport) int WINAPI GlobalUnlock(HANDLE h);
__declspec(dllimport) HANDLE WINAPI GlobalFree(HANDLE h);
__declspec(dllimport) unsigned long WINAPI GetEnvironmentVariableA(const char *name, char *buffer, unsigned long size);
__declspec(dllimport) HANDLE WINAPI CreateThread(void *attrs, unsigned long stack, unsigned long (WINAPI *start)(void *), void *param, unsigned long flags, unsigned long *id);
__declspec(dllimport) void WINAPI Sleep(unsigned long ms);
__declspec(dllimport) HMODULE WINAPI LoadLibraryA(const char *name);
__declspec(dllimport) FARPROC WINAPI GetProcAddress(HMODULE module, const char *name);

#pragma pack(push, 2)
typedef struct { int16_t Whole; uint16_t Frac; } FIX32;
typedef struct { uint16_t Major, Minor, Language, Country; char Info[34]; } VERSION_;
typedef struct {
    uint32_t Id; VERSION_ Version; uint16_t ProtocolMajor, ProtocolMinor; uint32_t SupportedGroups;
    char Manufacturer[34], ProductFamily[34], ProductName[34];
} IDENTITY;
typedef struct { uint16_t Cap, ConType; HANDLE hContainer; } CAPABILITY;
typedef struct { uint16_t ItemType; uint32_t Item; } ONEVALUE;
typedef struct { uint16_t ItemType; uint32_t NumItems; uint16_t Items[1]; } ARRAY_;
typedef struct { uint16_t ShowUI, ModalUI; HANDLE hParent; } USERINTERFACE;
typedef struct { uint16_t Count; uint32_t EOJ; } PENDINGXFERS;
typedef struct { uint32_t Min, Pref, Max; } SETUPMEMXFER;
typedef struct { FIX32 Left, Top, Right, Bottom; } FRAME;
typedef struct { FRAME Frame; uint32_t Doc, Page, FrameNum; } IMAGELAYOUT;
typedef struct {
    FIX32 X, Y; int32_t Width, Length; int16_t Samples, BitsPerSample[8], BitsPerPixel;
    uint16_t Planar; int16_t PixelType; uint16_t Compression;
} IMAGEINFO;
typedef struct { uint32_t Flags, Length; void *TheMem; } MEMORY;
typedef struct {
    uint16_t Compression; uint32_t BytesPerRow, Columns, Rows, XOffset, YOffset, BytesWritten; MEMORY Memory;
} IMAGEMEMXFER;
typedef struct { uint16_t CC, Data; } STATUS;
typedef struct { void *pEvent; uint16_t TWMessage; } EVENT_;
typedef struct { uint32_t Length; HANDLE hData; } CUSTOMDSDATA;
#pragma pack(pop)

enum { DG_CONTROL = 1, DG_IMAGE = 2 };
enum {
    DAT_CAPABILITY = 1, DAT_EVENT = 2, DAT_IDENTITY = 3, DAT_PENDINGXFERS = 5, DAT_SETUPMEMXFER = 6,
    DAT_STATUS = 8, DAT_USERINTERFACE = 9, DAT_XFERGROUP = 10, DAT_CUSTOMDSDATA = 12, DAT_NULL = 0,
    DAT_IMAGEINFO = 0x101, DAT_IMAGELAYOUT = 0x102, DAT_IMAGEMEMXFER = 0x103,
};
enum {
    MSG_GET = 1, MSG_GETCURRENT = 2, MSG_GETDEFAULT = 3, MSG_SET = 6, MSG_RESET = 7, MSG_QUERYSUPPORT = 8,
    MSG_XFERREADY = 0x101, MSG_CLOSEDSREQ = 0x102, MSG_CLOSEDSOK = 0x103, MSG_OPENDS = 0x401, MSG_CLOSEDS = 0x402,
    MSG_DISABLEDS = 0x501, MSG_ENABLEDS = 0x502, MSG_ENABLEDSUIONLY = 0x503, MSG_PROCESSEVENT = 0x601, MSG_ENDXFER = 0x701,
};
enum { RC_SUCCESS = 0, RC_FAILURE = 1, RC_CANCEL = 3, RC_NOTDSEVENT = 5, RC_XFERDONE = 6 };
enum { CC_SUCCESS = 0, CC_BUMMER = 1, CC_BADVALUE = 10, CC_CAPUNSUPPORTED = 13, CC_PAPERJAM = 20, CC_CHECKDEVICEONLINE = 23, CC_INTERLOCK = 24, CC_NOMEDIA = 29 };
enum { TW_INT16 = 1, TW_INT32 = 2, TW_UINT16 = 4, TW_UINT32 = 5, TW_BOOL = 6, TW_FIX32 = 7 };
enum { CON_ARRAY = 3, CON_ONEVALUE = 5 };
enum { QC_ALL = 0x7F };

#define WIDTH 301    /* odd, so strips need row padding */
#define HEIGHT 2000
#define PAD_TO 4
#define STRIP_ROWS 64

typedef struct { uint16_t id, type; uint32_t value; int readonly; } CAP;
static CAP caps[] = {
    { 0x0001, TW_INT16, 0xFFFF, 0 },  /* CAP_XFERCOUNT */
    { 0x0100, TW_UINT16, 0, 0 },      /* ICAP_COMPRESSION */
    { 0x0101, TW_UINT16, 2, 0 },      /* ICAP_PIXELTYPE: RGB until told otherwise */
    { 0x0102, TW_UINT16, 0, 0 },      /* ICAP_UNITS */
    { 0x0103, TW_UINT16, 0, 0 },      /* ICAP_XFERMECH */
    { 0x1002, TW_BOOL, 1, 0 },        /* CAP_FEEDERENABLED */
    { 0x1003, TW_BOOL, 1, 1 },        /* CAP_FEEDERLOADED */
    { 0x100B, TW_BOOL, 1, 0 },        /* CAP_INDICATORS */
    { 0x1015, TW_BOOL, 1, 1 },        /* CAP_CUSTOMDSDATA */
    { 0x1019, TW_BOOL, 1, 1 },        /* CAP_ENABLEDSUIONLY */
    { 0x1118, TW_FIX32, 200, 0 },     /* ICAP_XRESOLUTION */
    { 0x1119, TW_FIX32, 200, 0 },     /* ICAP_YRESOLUTION */
    { 0x111F, TW_UINT16, 0, 0 },      /* ICAP_PIXELFLAVOR */
    { 0x1122, TW_UINT16, 1, 0 },      /* ICAP_SUPPORTEDSIZES */
    { 0x112B, TW_UINT16, 24, 0 },     /* ICAP_BITDEPTH */
    { 0x112D, TW_BOOL, 0, 0 },        /* ICAP_UNDEFINEDIMAGESIZE */
    { 0x1134, TW_INT32, 0, 0 },       /* ICAP_AUTODISCARDBLANKPAGES */
    { 0x1150, TW_BOOL, 1, 0 },        /* ICAP_AUTOMATICBORDERDETECTION */
    { 0x1151, TW_BOOL, 1, 0 },        /* ICAP_AUTOMATICDESKEW */
    { 0x8042, TW_UINT16, 0, 0 },      /* an unadvertised vendor capability */
};
#define CAP_COUNT (sizeof(caps) / sizeof(caps[0]))

static char mode[32];
static uint16_t status = CC_SUCCESS;
static IDENTITY *appIdentity;
static uint16_t (WINAPI *dsmEntry)(IDENTITY *, IDENTITY *, uint32_t, uint16_t, uint16_t, void *);
static IDENTITY self;
static FRAME frame = { { 0, 0 }, { 0, 0 }, { 8, 0 }, { 14, 0 } };
static int row, xferDone, uiOnly;
static uint32_t customData = 0x11111111;

static int mode_is(const char *m) {
    const char *a = mode;
    while (*a && *a == *m) { a++; m++; }
    return *a == *m;
}
static void copy_str(char *d, const char *s) { while ((*d++ = *s++)) { } }
static void *memset_(void *d, int v, unsigned n) { unsigned char *p = d; while (n--) *p++ = (unsigned char)v; return d; }
void *memset(void *d, int v, unsigned n) { return memset_(d, v, n); }
void *memcpy(void *d, const void *s, unsigned n) { unsigned char *a = d; const unsigned char *b = s; while (n--) *a++ = *b++; return d; }

static CAP *find_cap(uint16_t id) {
    for (unsigned i = 0; i < CAP_COUNT; i++) if (caps[i].id == id) return &caps[i];
    return 0;
}

static HANDLE alloc_onevalue(uint16_t type, uint32_t item) {
    HANDLE h = GlobalAlloc(0x42, sizeof(ONEVALUE));
    ONEVALUE *v = GlobalLock(h);
    v->ItemType = type;
    v->Item = type == TW_FIX32 ? (item & 0xFFFF) : item;
    GlobalUnlock(h);
    return h;
}

static uint16_t handle_cap(CAPABILITY *c, uint16_t msg) {
    if (c->Cap == 0x1005 && msg != MSG_QUERYSUPPORT) { /* CAP_SUPPORTEDCAPS: all except the vendor one */
        HANDLE h = GlobalAlloc(0x42, sizeof(ARRAY_) + CAP_COUNT * 2);
        ARRAY_ *a = GlobalLock(h);
        a->ItemType = TW_UINT16;
        a->NumItems = 0;
        for (unsigned i = 0; i + 1 < CAP_COUNT; i++) a->Items[a->NumItems++] = caps[i].id;
        GlobalUnlock(h);
        c->ConType = CON_ARRAY;
        c->hContainer = h;
        return RC_SUCCESS;
    }

    CAP *cap = find_cap(c->Cap);
    if (msg == MSG_QUERYSUPPORT) {
        if (!cap && c->Cap != 0x1005) { status = CC_CAPUNSUPPORTED; return RC_FAILURE; }
        c->ConType = CON_ONEVALUE;
        c->hContainer = alloc_onevalue(TW_INT32, QC_ALL);
        return RC_SUCCESS;
    }

    if (!cap) { status = CC_CAPUNSUPPORTED; return RC_FAILURE; }

    /* "unconfirmed" models a driver that refuses every route to Long Paper. */
    if (mode_is("unconfirmed") && msg == MSG_SET && c->Cap == 0x112D) { status = CC_BADVALUE; return RC_FAILURE; }

    switch (msg) {
    case MSG_GET: case MSG_GETCURRENT: case MSG_GETDEFAULT:
        c->ConType = CON_ONEVALUE;
        c->hContainer = alloc_onevalue(cap->type, cap->value);
        return RC_SUCCESS;
    case MSG_SET: {
        if (cap->readonly) { status = CC_BADVALUE; return RC_FAILURE; }
        ONEVALUE *v = GlobalLock(c->hContainer);
        cap->value = v->Item;
        if (cap->type == TW_FIX32) cap->value = v->Item & 0xFFFF;
        GlobalUnlock(c->hContainer);
        return RC_SUCCESS;
    }
    case MSG_RESET:
        return RC_SUCCESS;
    }
    status = CC_BUMMER;
    return RC_FAILURE;
}

static unsigned long WINAPI notify_later(void *param) {
    Sleep(50);
    dsmEntry(&self, appIdentity, DG_CONTROL, DAT_NULL, (uint16_t)(uintptr_t)param, 0);
    return 0;
}

/* The protocol requires ENABLEDS to return before the source sends any message. */
static void notify(uint16_t msg) {
    CreateThread(0, 0, notify_later, (void *)(uintptr_t)msg, 0, 0);
}

static int is_gray(void) { return find_cap(0x101)->value == 1; }
static uint32_t channels(void) { return is_gray() ? 1 : 3; }
static uint32_t stride(void) { uint32_t b = WIDTH * channels(); return (b + PAD_TO - 1) / PAD_TO * PAD_TO; }

static uint8_t pixel(int x, int y, unsigned c) {
    uint8_t value = (uint8_t)(20 + ((x * 3 + y * 7 + (int)c * 11) % 200));
    return mode_is("vanilla") ? (uint8_t)(255 - value) : value;
}

static uint16_t handle_memxfer(IMAGEMEMXFER *m) {
    int jamAt = mode_is("jam") || mode_is("offline") || mode_is("cover") || mode_is("cancel") ? HEIGHT / 3 : -1;
    int total = mode_is("short") ? HEIGHT / 2 : HEIGHT;
    if (jamAt >= 0 && row >= jamAt) {
        if (mode_is("cancel")) return RC_CANCEL;
        status = mode_is("jam") ? CC_PAPERJAM : mode_is("cover") ? CC_INTERLOCK : CC_CHECKDEVICEONLINE;
        return RC_FAILURE;
    }

    uint32_t s = stride(), ch = channels();
    uint32_t rows = HEIGHT - row < STRIP_ROWS ? HEIGHT - row : STRIP_ROWS;
    if (total < HEIGHT && (uint32_t)(total - row) < rows) rows = total - row;
    if (rows * s > m->Memory.Length) rows = m->Memory.Length / s;

    uint8_t *out = m->Memory.TheMem;
    for (uint32_t r = 0; r < rows; r++) {
        uint8_t *line = out + r * s;
        for (uint32_t x = 0; x < WIDTH; x++)
            for (uint32_t c = 0; c < ch; c++)
                line[x * ch + c] = pixel((int)x, row + (int)r, c);
        for (uint32_t i = WIDTH * ch; i < s; i++) line[i] = 0xEE; /* padding must not reach the image */
    }

    m->Compression = 0;
    m->BytesPerRow = s;
    m->Columns = WIDTH;
    m->Rows = rows;
    m->XOffset = 0;
    m->YOffset = (uint32_t)row;
    m->BytesWritten = rows * s;
    row += (int)rows;
    if (row >= total) { xferDone = 1; return RC_XFERDONE; }
    return RC_SUCCESS;
}

__declspec(dllexport) uint16_t WINAPI DS_Entry(IDENTITY *origin, uint32_t dg, uint16_t dat, uint16_t msg, void *data) {
    if (dg == DG_CONTROL) {
        switch (dat) {
        case DAT_IDENTITY: {
            IDENTITY *id = data;
            if (msg == MSG_GET) {
                memset(id, 0, sizeof *id);
                id->Version.Major = 1;
                copy_str(id->Version.Info, "fake-1.0");
                id->ProtocolMajor = 1;
                id->ProtocolMinor = 9;
                id->SupportedGroups = 3;
                copy_str(id->Manufacturer, "Fake");
                copy_str(id->ProductFamily, "Fake");
                copy_str(id->ProductName, "TW-Brother DS-640");
                return RC_SUCCESS;
            }
            if (msg == MSG_OPENDS) {
                GetEnvironmentVariableA("FAKE_DS_MODE", mode, sizeof mode);
                dsmEntry = (void *)GetProcAddress(LoadLibraryA("C:\\windows\\twain_32.dll"), "DSM_Entry");
                self = *id;
                appIdentity = origin;
                if (mode_is("vanilla")) find_cap(0x111F)->value = 1;
                if (mode_is("noopen")) { status = CC_CHECKDEVICEONLINE; return RC_FAILURE; }
                return RC_SUCCESS;
            }
            if (msg == MSG_CLOSEDS) return RC_SUCCESS;
            break;
        }
        case DAT_CAPABILITY:
            return handle_cap(data, msg);
        case DAT_STATUS:
            ((STATUS *)data)->CC = status;
            ((STATUS *)data)->Data = 0;
            status = CC_SUCCESS;
            return RC_SUCCESS;
        case DAT_EVENT:
            ((EVENT_ *)data)->TWMessage = 0;
            return RC_NOTDSEVENT;
        case DAT_XFERGROUP:
            *(uint32_t *)data = DG_IMAGE;
            return RC_SUCCESS;
        case DAT_USERINTERFACE:
            if (msg == MSG_ENABLEDS || msg == MSG_ENABLEDSUIONLY) {
                row = 0;
                xferDone = 0;
                uiOnly = msg == MSG_ENABLEDSUIONLY;
                if (uiOnly) {
                    /* The "user" selects Long Paper and clicks OK, or cancels. */
                    if (mode_is("dialogcancel")) { notify(MSG_CLOSEDSREQ); return RC_SUCCESS; }
                    find_cap(0x112D)->value = 1;
                    frame.Bottom.Whole = 72;
                    customData = 0x22222222;
                    notify(MSG_CLOSEDSOK);
                    return RC_SUCCESS;
                }
                if (mode_is("nopaper")) { status = CC_NOMEDIA; return RC_FAILURE; }
                notify(MSG_XFERREADY);
                return RC_SUCCESS;
            }
            if (msg == MSG_DISABLEDS) return RC_SUCCESS;
            break;
        case DAT_PENDINGXFERS: {
            PENDINGXFERS *p = data;
            p->EOJ = 0;
            if (msg == MSG_GET) { p->Count = xferDone ? 0 : 1; return RC_SUCCESS; }
            /* "twopages" keeps offering pages, as a batch feeder would. */
            p->Count = mode_is("twopages") && msg == MSG_ENDXFER && row < HEIGHT * 2 ? 1 : 0;
            if (p->Count) { row = HEIGHT * 2; xferDone = 0; }
            return RC_SUCCESS;
        }
        case DAT_SETUPMEMXFER: {
            SETUPMEMXFER *s = data;
            s->Min = 16384; s->Pref = 65536; s->Max = 1048576;
            return RC_SUCCESS;
        }
        case DAT_CUSTOMDSDATA: {
            CUSTOMDSDATA *c = data;
            if (msg == MSG_GET) {
                c->Length = 4;
                c->hData = GlobalAlloc(0x42, 4);
                uint32_t *p = GlobalLock(c->hData);
                *p = customData;
                GlobalUnlock(c->hData);
                return RC_SUCCESS;
            }
            if (msg == MSG_SET) {
                if (mode_is("rejectprofile")) { status = CC_BADVALUE; return RC_FAILURE; }
                uint32_t *p = GlobalLock(c->hData);
                customData = *p;
                GlobalUnlock(c->hData);
                if (customData == 0x22222222) { find_cap(0x112D)->value = 1; }
                return RC_SUCCESS;
            }
            break;
        }
        }
    } else if (dg == DG_IMAGE) {
        switch (dat) {
        case DAT_IMAGELAYOUT: {
            IMAGELAYOUT *l = data;
            if (msg == MSG_GET || msg == MSG_GETCURRENT || msg == MSG_GETDEFAULT) {
                memset(l, 0, sizeof *l);
                l->Frame = frame;
                return RC_SUCCESS;
            }
            if (msg == MSG_SET) {
                if (mode_is("clamp") || mode_is("unconfirmed")) { /* clamp to 14 in, as Linux's driver limit does */
                    if (l->Frame.Bottom.Whole > 14) l->Frame.Bottom.Whole = 14;
                }
                frame = l->Frame;
                return RC_SUCCESS;
            }
            break;
        }
        case DAT_IMAGEINFO: {
            IMAGEINFO *i = data;
            memset(i, 0, sizeof *i);
            i->X.Whole = (int16_t)find_cap(0x1118)->value;
            i->Y.Whole = (int16_t)find_cap(0x1119)->value;
            i->Width = WIDTH;
            i->Length = mode_is("undefined") ? -1 : HEIGHT;
            i->Samples = (int16_t)channels();
            i->BitsPerSample[0] = 8;
            i->BitsPerSample[1] = i->BitsPerSample[2] = is_gray() ? 0 : 8;
            i->BitsPerPixel = (int16_t)(channels() * 8);
            i->PixelType = (int16_t)find_cap(0x101)->value;
            return RC_SUCCESS;
        }
        case DAT_IMAGEMEMXFER:
            return handle_memxfer(data);
        }
    }

    status = CC_BUMMER;
    return RC_FAILURE;
}

int WINAPI DllMainCRTStartup(void *a, unsigned b, void *c) { (void)a; (void)b; (void)c; return 1; }
