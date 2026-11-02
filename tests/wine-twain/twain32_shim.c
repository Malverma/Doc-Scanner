/*
 * Stands in for twain_32.dll in the test prefix. Forwards everything to Wine's TWAIN manager, but hides its
 * TWAIN 2 support (DF_DSM2), which Microsoft's twain_32.dll doesn't advertise. Wine's version supplies a
 * movable-memory allocator that NTwain misuses, so without this the test would fail in a way real Windows can't.
 *
 * It also carries MSG_CLOSEDSOK, which Wine's manager drops: the message travels through Wine as MSG_CLOSEDSREQ
 * and is changed back when the app collects it.
 */
#include <stdint.h>
#define WINAPI __stdcall
__declspec(dllimport) void *WINAPI LoadLibraryA(const char *name);
__declspec(dllimport) void *WINAPI GetProcAddress(void *module, const char *name);

typedef uint16_t (WINAPI *DSM_ENTRY)(void *, void *, uint32_t, uint16_t, uint16_t, void *);
enum { DAT_NULL = 0, DAT_EVENT = 2, DAT_PARENT = 4, MSG_CLOSEDSREQ = 0x102, MSG_CLOSEDSOK = 0x103, MSG_OPENDSM = 0x301, SUPPORTED_GROUPS_OFFSET = 50 };
enum { DAT_CALLBACK = 0x10, DAT_CALLBACK2 = 0x12, RC_FAILURE = 1, RC_DSEVENT = 4 };
#pragma pack(push, 2)
typedef struct { void *pEvent; uint16_t TWMessage; } EVENT_;
#pragma pack(pop)
static volatile int closeWasOk;
#define DF_DSM2 0x10000000u

__declspec(dllexport) uint16_t WINAPI DSM_Entry(void *origin, void *dest, uint32_t dg, uint16_t dat, uint16_t msg, void *data) {
    static DSM_ENTRY real;
    if (!real) real = (DSM_ENTRY)GetProcAddress(LoadLibraryA("twaindsm.dll"), "DSM_Entry");
    /* Without callbacks, source messages reach the app through its window, as with Microsoft's manager. */
    if (dat == DAT_CALLBACK || dat == DAT_CALLBACK2) return RC_FAILURE;
    if (dat == DAT_NULL && msg == MSG_CLOSEDSOK) { closeWasOk = 1; msg = MSG_CLOSEDSREQ; }
    uint16_t rc = real(origin, dest, dg, dat, msg, data);
    if (dat == DAT_EVENT && rc == RC_DSEVENT && closeWasOk && ((EVENT_ *)data)->TWMessage == MSG_CLOSEDSREQ) {
        ((EVENT_ *)data)->TWMessage = MSG_CLOSEDSOK;
        closeWasOk = 0;
    }
    if (dat == DAT_PARENT && msg == MSG_OPENDSM)
        *(uint32_t *)((char *)origin + SUPPORTED_GROUPS_OFFSET) &= ~DF_DSM2;
    return rc;
}

int WINAPI DllMainCRTStartup(void *a, unsigned b, void *c) { (void)a; (void)b; (void)c; return 1; }
