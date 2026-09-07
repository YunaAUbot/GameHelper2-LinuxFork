#include <GL/gl.h>
#include <GL/glx.h>
#include <X11/Xatom.h>
#include <X11/Xlib.h>
#include <X11/Xutil.h>
#include <X11/XKBlib.h>
#include <X11/keysym.h>
#include <X11/extensions/Xrender.h>
#include <X11/extensions/shape.h>
#include <arpa/inet.h>
#include <errno.h>
#include <fcntl.h>
#include <netinet/in.h>
#include <poll.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <locale.h>
#include <limits.h>
#include <math.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <time.h>
#include <unistd.h>

/* Native ARGB GLX compositor for the experimental backend.  Wine sends ImGui
 * draw lists, not monitor-sized pixels.  This deliberately uses fixed GL so
 * it works with the GLX implementation Proton already exposes. */
#define FRAME_MAGIC 0x31464745u /* EGF1, little endian */
#define FONT_MAGIC  0x31415445u /* ETA1, little endian */
#define TEXTURE_MAGIC 0x31585445u /* ETX1, little endian */
#define TEXTURE_DELETE_MAGIC 0x31445445u /* ETD1, little endian */
#define TEXTURE_ACK_MAGIC 0x314B5458u /* XTK1, little endian */
#define INPUT_MODE_MAGIC 0x31435345u
#define MOUSE_INPUT_MAGIC 0x31494e45u
#define KEY_INPUT_MAGIC 0x314b4e45u
#define KEYBOARD_MODE_MAGIC 0x314b5345u
#define SHUTDOWN_MAGIC 0x31545845u /* EXT1, authenticated deliberate exit */
#define AUTH_MAGIC 0x31485541u
#define READY_MAGIC 0x31594452u
#define AUTH_TOKEN_BYTES 32u
#define MAX_OVERLAY_DIMENSION 32767
#define MIN_OVERLAY_POSITION (-32768)
#define MAX_OVERLAY_POSITION 32767
#define MAX_TEXTURE_WIDTH 4096u
#define MAX_TEXTURE_HEIGHT 8192u
#define MAX_TEXTURE_BYTES (32u * 1024u * 1024u)
#define MAX_TEXTURE_TOTAL_BYTES (64u * 1024u * 1024u)
#define MAX_TEXTURE_COUNT 64u
#define KEYBOARD_RETRY_SECONDS 0.25
#define AUTHENTICATED_RECONNECT_GRACE_SECONDS 300.0
static double now_seconds(void) { struct timespec v; clock_gettime(CLOCK_MONOTONIC, &v); return v.tv_sec + v.tv_nsec / 1e9; }
static double wall_seconds(void) { struct timespec v; clock_gettime(CLOCK_REALTIME, &v); return v.tv_sec + v.tv_nsec / 1e9; }
static void trace_input(const char *event, int a, int b) { FILE *f=fopen("/tmp/gamehelper2-gpu-input.log","a"); if(f){fprintf(f,"%.3f %s %d %d\n",now_seconds(),event,a,b);fclose(f);} }
static void trace_renderer(void) {
    const char *vendor=(const char*)glGetString(GL_VENDOR);
    const char *renderer=(const char*)glGetString(GL_RENDERER);
    const char *version=(const char*)glGetString(GL_VERSION);
    FILE *f=fopen("/tmp/gamehelper2-gpu-renderer.log","a");
    if(f){fprintf(f,"%.3f pid=%ld vendor=%s renderer=%s version=%s\n",now_seconds(),(long)getpid(),vendor?vendor:"unknown",renderer?renderer:"unknown",version?version:"unknown");fclose(f);}
}
static void trace_renderer_event(const char *event, double heartbeat_age, int client) {
    FILE *f=fopen("/tmp/gamehelper2-gpu-renderer.log","a");
    if(f){fprintf(f,"%.3f pid=%ld event=%s heartbeat_age=%.3f client=%d\n",now_seconds(),(long)getpid(),event,heartbeat_age,client);fclose(f);}
}
static uint32_t u32(const unsigned char **p) { uint32_t v; memcpy(&v,*p,4); *p+=4; return v; }
static uint64_t u64(const unsigned char **p) { uint64_t v; memcpy(&v,*p,8); *p+=8; return v; }
static float f32(const unsigned char **p) { float v; memcpy(&v,*p,4); *p+=4; return v; }
static int multiply_size(size_t a,size_t b,size_t*out){if(a&&b>SIZE_MAX/a)return 0;*out=a*b;return 1;}
static int parse_duration(const char *text,double *duration){char *end=NULL;errno=0;double value=strtod(text,&end);if(errno||end==text||*end!='\0'||!isfinite(value)||value<0)return 0;*duration=value;return 1;}
static int valid_dimensions(int width, int height) { return width>0&&height>0&&width<=MAX_OVERLAY_DIMENSION&&height<=MAX_OVERLAY_DIMENSION; }
static int valid_geometry(int x,int y,int width,int height){return x>=MIN_OVERLAY_POSITION&&x<=MAX_OVERLAY_POSITION&&y>=MIN_OVERLAY_POSITION&&y<=MAX_OVERLAY_POSITION&&valid_dimensions(width,height);}
static Window find_named_window(Display *d, Window root, const char *title) { char *name=NULL; if(XFetchName(d,root,&name)>0 && name){int match=!strcmp(name,title);XFree(name);if(match)return root;} Window r,p,*children=NULL,found=None;unsigned count=0;if(!XQueryTree(d,root,&r,&p,&children,&count))return None;for(unsigned i=0;i<count&&!found;i++)found=find_named_window(d,children[i],title);if(children)XFree(children);return found; }
static int poe_geometry(Display *d, int screen, int *x, int *y, int *width, int *height) {
    Atom list_atom=XInternAtom(d,"_NET_CLIENT_LIST",False), actual; int format; unsigned long count,after; unsigned char *raw=NULL;
    if(XGetWindowProperty(d,RootWindow(d,screen),list_atom,0,4096,False,XA_WINDOW,&actual,&format,&count,&after,&raw)!=Success || !raw) return 0;
    Window *windows=(Window*)raw; int found=0;
    for(unsigned long i=0;i<count;i++) { char *name=NULL; if(XFetchName(d,windows[i],&name)>0 && name) { if(strcmp(name,"Path of Exile 2")==0) { XWindowAttributes a; Window child; int rx,ry; if(XGetWindowAttributes(d,windows[i],&a) && XTranslateCoordinates(d,windows[i],RootWindow(d,screen),0,0,&rx,&ry,&child) && valid_geometry(rx,ry,a.width,a.height)) { *x=rx;*y=ry;*width=a.width;*height=a.height;found=1; } XFree(name);break; } XFree(name); } }
    XFree(raw); if(found)return 1;
    Window poe=find_named_window(d,RootWindow(d,screen),"Path of Exile 2");
    if(poe){XWindowAttributes a;Window child;int rx,ry;if(XGetWindowAttributes(d,poe,&a)&&XTranslateCoordinates(d,poe,RootWindow(d,screen),0,0,&rx,&ry,&child)&&valid_geometry(rx,ry,a.width,a.height)){*x=rx;*y=ry;*width=a.width;*height=a.height;return 1;}}
    return 0;
}
/* TCP may split one ImGui frame across reads.  Once its four-byte length has
 * arrived, wait for the rest rather than discarding a partial payload; the
 * managed side writes every payload atomically to this local connection. */
static int read_exact(int fd, void *out, size_t bytes) { unsigned char *p=out;while(bytes) { ssize_t n=recv(fd,p,bytes,0); if(n>0){p+=n;bytes-=(size_t)n;continue;} if(n==0)return 0; if(errno==EINTR)continue; if(errno==EAGAIN||errno==EWOULDBLOCK)return -1; return -1;} return 1; }
static int write_exact(int fd, const void *data, size_t bytes) { const unsigned char *p=data;while(bytes) { ssize_t n=send(fd,p,bytes,MSG_NOSIGNAL); if(n>0){p+=n;bytes-=(size_t)n;continue;} if(n<0&&errno==EINTR)continue; if(n<0&&(errno==EAGAIN||errno==EWOULDBLOCK))return 0; return 0;} return 1; }
static int decode_token(const char *hex, unsigned char token[AUTH_TOKEN_BYTES]) { if(strlen(hex)!=AUTH_TOKEN_BYTES*2)return 0; for(size_t i=0;i<AUTH_TOKEN_BYTES;i++){unsigned value;if(sscanf(hex+i*2,"%2x",&value)!=1)return 0;token[i]=(unsigned char)value;}return 1; }
static int authenticate_client(int fd, const unsigned char token[AUTH_TOKEN_BYTES]) { uint32_t len; unsigned char msg[4+AUTH_TOKEN_BYTES]; if(read_exact(fd,&len,4)<=0||len!=sizeof msg||read_exact(fd,msg,sizeof msg)<=0)return 0; const unsigned char*p=msg;if(u32(&p)!=AUTH_MAGIC)return 0;unsigned diff=0;for(size_t i=0;i<AUTH_TOKEN_BYTES;i++)diff|=p[i]^token[i];if(diff)return 0;uint32_t ready=READY_MAGIC;return write_exact(fd,&ready,sizeof ready); }
static void set_input(Display *d, Window w, int width, int height, int interactive) { if(interactive&&valid_dimensions(width,height)) { XRectangle r={0,0,(unsigned short)width,(unsigned short)height}; XShapeCombineRectangles(d,w,ShapeInput,0,0,&r,1,ShapeSet,Unsorted); } else XShapeCombineRectangles(d,w,ShapeInput,0,0,NULL,0,ShapeSet,Unsorted); trace_input("mode",interactive,0); XFlush(d); }
/* The managed side consumes a four-byte byte-count followed by a 20-byte
 * payload: magic, button, down, x, y.  Keep the framing separate from the
 * payload; the earlier five-word buffer overwrote `down` with x and made the
 * receiver consume the beginning of the following mouse event as payload. */
static void send_mouse(int fd, int button, int down, int x, int y) {
    uint32_t msg[6] = { 20, MOUSE_INPUT_MAGIC, (uint32_t)button, (uint32_t)down, 0, 0 };
    float point[2] = { (float)x, (float)y };
    memcpy(&msg[4], point, sizeof point);
    (void)write_exact(fd, msg, sizeof msg);
}
static uint32_t utf8_codepoint(const char *s, int length) {
    const unsigned char *p=(const unsigned char*)s;
    if(length<=0) return 0;
    if(p[0]<0x80) return p[0];
    if((p[0]&0xe0)==0xc0 && length>=2) return ((p[0]&0x1f)<<6)|(p[1]&0x3f);
    if((p[0]&0xf0)==0xe0 && length>=3) return ((p[0]&0x0f)<<12)|((p[1]&0x3f)<<6)|(p[2]&0x3f);
    if((p[0]&0xf8)==0xf0 && length>=4) return ((p[0]&0x07)<<18)|((p[1]&0x3f)<<12)|((p[2]&0x3f)<<6)|(p[3]&0x3f);
    return 0;
}
static void send_key(int fd, KeySym key, int down, uint32_t codepoint) {
    uint32_t msg[6] = { 20, KEY_INPUT_MAGIC, (uint32_t)key, (uint32_t)down, codepoint, 0 };
    (void)write_exact(fd, msg, sizeof msg);
}
static int send_texture_ack(int fd, uint32_t operation, int success, uint64_t id) {
    uint32_t msg[6] = { 20, TEXTURE_ACK_MAGIC, operation, success ? 1u : 0u, (uint32_t)id, (uint32_t)(id >> 32) };
    return write_exact(fd, msg, sizeof msg);
}
struct keyboard_capture { Window focus_window; Atom protocols; Atom delete_window; Time user_time; int requested; int active; int suspended; double retry_at; unsigned char down[256]; };

/* XGrabKeyboard alone only redirects events already delivered to Xwayland.
 * If a Wayland app (e.g. the browser used to copy a URL) has desktop focus,
 * GrabSuccess does not make ordinary text reach our non-focusable compositor.
 * A transparent, pointer-transparent managed window gives the text editor
 * normal application focus, without changing the visible overlay's stacking.
 * Map it only for WantTextInput and let the WM restore focus when it unmaps. */
static void focus_text_input(Display *d, Window compositor, struct keyboard_capture *state) {
    if(!state->focus_window) {
        XWindowAttributes attrs;
        XGetWindowAttributes(d,compositor,&attrs);
        XSetWindowAttributes wa={0};
        wa.colormap=attrs.colormap;
        wa.border_pixel=wa.background_pixel=0; /* ARGB, including zero alpha. */
        Window w=XCreateWindow(d,DefaultRootWindow(d),0,0,1,1,0,attrs.depth,InputOutput,attrs.visual,
                              CWColormap|CWBorderPixel|CWBackPixel,&wa);
        state->focus_window=w;
        XSelectInput(d,w,FocusChangeMask);
        XStoreName(d,w,"GameHelper2 text input");
        state->protocols=XInternAtom(d,"WM_PROTOCOLS",False);
        state->delete_window=XInternAtom(d,"WM_DELETE_WINDOW",False);
        XSetWMProtocols(d,w,&state->delete_window,1);
        XClassHint class_hint={"gamehelper2-text-input","GameHelper2"};
        XSetClassHint(d,w,&class_hint);
        unsigned long motif[5]={2,0,0,0,0}; /* Undecorated. */
        Atom motif_atom=XInternAtom(d,"_MOTIF_WM_HINTS",False);
        XChangeProperty(d,w,motif_atom,motif_atom,32,PropModeReplace,(unsigned char*)motif,5);
        Atom skip[2]={XInternAtom(d,"_NET_WM_STATE_SKIP_TASKBAR",False),XInternAtom(d,"_NET_WM_STATE_SKIP_PAGER",False)};
        XChangeProperty(d,w,XInternAtom(d,"_NET_WM_STATE",False),XA_ATOM,32,PropModeReplace,(unsigned char*)skip,2);
        XShapeCombineRectangles(d,w,ShapeInput,0,0,NULL,0,ShapeSet,Unsorted);
    }
    XChangeProperty(d,state->focus_window,XInternAtom(d,"_NET_WM_USER_TIME",False),XA_CARDINAL,32,
                    PropModeReplace,(unsigned char*)&state->user_time,1);
    XMapRaised(d,state->focus_window);
    XEvent event={0};
    event.xclient.type=ClientMessage;
    event.xclient.window=state->focus_window;
    event.xclient.message_type=XInternAtom(d,"_NET_ACTIVE_WINDOW",False);
    event.xclient.format=32;
    event.xclient.data.l[0]=1; /* Application request, not a pager/WM override. */
    event.xclient.data.l[1]=(long)state->user_time;
    XSendEvent(d,DefaultRootWindow(d),False,SubstructureRedirectMask|SubstructureNotifyMask,&event);
}
static void release_keyboard(Display *d, int fd, struct keyboard_capture *state) {
    if(state->active) {
        for(size_t i=0;i<sizeof state->down;i++) if(state->down[i])
            send_key(fd,XkbKeycodeToKeysym(d,(KeyCode)i,0,0),0,0);
        XUngrabKeyboard(d,CurrentTime);
        if(state->focus_window) XUnmapWindow(d,state->focus_window);
        trace_input("keyboard",0,0);
    }
    memset(state->down,0,sizeof state->down);
    state->active=0;
    XFlush(d);
}
static void request_keyboard(Display *d, Window w, int fd, struct keyboard_capture *state, int capture) {
    state->requested=capture;
    if(!capture) { state->suspended=0; state->retry_at=0; release_keyboard(d,fd,state); return; }
    if(!state->suspended && !state->active && now_seconds()>=state->retry_at) {
        int result=XGrabKeyboard(d,w,False,GrabModeAsync,GrabModeAsync,CurrentTime);
        if(result==GrabSuccess) { state->active=1; focus_text_input(d,w,state); }
        else state->retry_at=now_seconds()+KEYBOARD_RETRY_SECONDS;
        trace_input("keyboard",capture,result);
        XFlush(d);
    }
}
static void close_client(Display *d, int *fd, struct keyboard_capture *keyboard) {
    release_keyboard(d,*fd,keyboard);
    keyboard->requested=0;
    keyboard->suspended=0;
    keyboard->retry_at=0;
    if(*fd>=0) close(*fd);
    *fd=-1;
}
static int passive_grab_error;
static int record_passive_grab_error(Display *d, XErrorEvent *event) { (void)d; passive_grab_error=event->error_code; return 0; }

struct native_texture { uint64_t id; GLuint gl; size_t bytes; struct native_texture *next; };
struct native_textures { struct native_texture *head; size_t bytes; unsigned count; };
struct native_texture_upload { uint64_t id; uint32_t width,height,total,received; unsigned char *pixels; };
static void reset_texture_upload(struct native_texture_upload *upload) { free(upload->pixels);memset(upload,0,sizeof *upload); }
static void close_texture_client(Display *d, int *fd, struct keyboard_capture *keyboard, struct native_texture_upload *upload) { close_client(d,fd,keyboard);reset_texture_upload(upload); }
static struct native_texture *find_texture(struct native_textures *textures, uint64_t id) {
    for(struct native_texture *item=textures->head;item;item=item->next)if(item->id==id)return item;
    return NULL;
}
static int upload_texture(struct native_textures *textures, uint64_t id, uint32_t width, uint32_t height, uint32_t bytes, const unsigned char *pixels) {
    if(id<=1||width==0||height==0||width>MAX_TEXTURE_WIDTH||height>MAX_TEXTURE_HEIGHT||
       width>UINT32_MAX/height||width*height>UINT32_MAX/4u||bytes!=width*height*4u||bytes>MAX_TEXTURE_BYTES)return 0;
    struct native_texture *item=find_texture(textures,id); size_t old=item?item->bytes:0;
    if((!item&&textures->count>=MAX_TEXTURE_COUNT)||textures->bytes-old>MAX_TEXTURE_TOTAL_BYTES-bytes)return 0;
    GLuint replacement=0;while(glGetError()!=GL_NO_ERROR){}glGenTextures(1,&replacement);if(!replacement||glGetError()!=GL_NO_ERROR)return 0;
    glBindTexture(GL_TEXTURE_2D,replacement);glTexParameteri(GL_TEXTURE_2D,GL_TEXTURE_MIN_FILTER,GL_LINEAR);glTexParameteri(GL_TEXTURE_2D,GL_TEXTURE_MAG_FILTER,GL_LINEAR);glTexParameteri(GL_TEXTURE_2D,GL_TEXTURE_WRAP_S,GL_CLAMP_TO_EDGE);glTexParameteri(GL_TEXTURE_2D,GL_TEXTURE_WRAP_T,GL_CLAMP_TO_EDGE);glPixelStorei(GL_UNPACK_ALIGNMENT,1);glTexImage2D(GL_TEXTURE_2D,0,GL_RGBA,(GLsizei)width,(GLsizei)height,0,GL_RGBA,GL_UNSIGNED_BYTE,pixels);
    if(glGetError()!=GL_NO_ERROR){glDeleteTextures(1,&replacement);return 0;}
    if(!item){item=calloc(1,sizeof *item);if(!item){glDeleteTextures(1,&replacement);return 0;}item->id=id;item->next=textures->head;textures->head=item;textures->count++;}
    else glDeleteTextures(1,&item->gl);
    item->gl=replacement;textures->bytes=textures->bytes-old+bytes;item->bytes=bytes;return 1;
}
static int delete_texture(struct native_textures *textures, uint64_t id) {
    struct native_texture **link=&textures->head;
    while(*link){struct native_texture *item=*link;if(item->id==id){*link=item->next;textures->bytes-=item->bytes;textures->count--;glDeleteTextures(1,&item->gl);free(item);return 1;}link=&item->next;}return 0;
}
static void delete_all_textures(struct native_textures *textures) { while(textures->head)delete_texture(textures,textures->head->id); }
/* MotionNotify is only generated while the pointer is already inside the
 * current ShapeInput region.  Query the X server once per compositor cycle as
 * the authoritative position source instead, so ImGui can correctly retain
 * hover/capture while that region changes between frames. */
static void send_pointer_position(Display *d, Window w, int fd) {
    Window root, child;
    int root_x, root_y, win_x, win_y;
    unsigned int mask;
    if (fd >= 0 && XQueryPointer(d, w, &root, &child, &root_x, &root_y, &win_x, &win_y, &mask))
        send_mouse(fd, -1, 0, win_x, win_y);
}
static double heartbeat_timestamp_seconds(const char *path) { FILE *f=fopen(path,"r"); long long stamp=0; if(f){if(fscanf(f,"%lld",&stamp)!=1)stamp=0;fclose(f);} return stamp<0?-1.0:(stamp>0?(double)stamp/1000.0:0.0); }
static int heartbeat_state(const char *path, int *x, int *y, int *width, int *height) { FILE *f=fopen(path,"r"); long long stamp=0; int mode=0; if(f){if(fscanf(f,"%lld %d %d %d %d %d",&stamp,&mode,x,y,width,height)!=6)mode=0;fclose(f);} (void)stamp; return mode!=0; }
/* Make only large, solid ImGui primitives receptive to input.  Dear ImGui
 * emits the menu/window background as two large filled triangles; text-only
 * overlays (e.g. ground-item labels) are made of tiny glyph triangles and
 * remain outside this region, so they keep click-through behaviour. */
static void add_input_triangle(Region region, const unsigned char *verts, uint32_t nv,
                               uint32_t a, uint32_t b, uint32_t c,
                               float display_x, float display_y, int width, int height) {
    if (a >= nv || b >= nv || c >= nv) return;
    const unsigned char *va = verts + (size_t)a * 20, *vb = verts + (size_t)b * 20, *vc = verts + (size_t)c * 20;
    /* ImDrawVert is Pos.xy, Uv.xy, ImU32 col (little-endian RGBA here). */
    if (va[19] < 96 || vb[19] < 96 || vc[19] < 96) return;
    float ax, ay, bx, by, cx, cy;
    memcpy(&ax, va, 4); memcpy(&ay, va + 4, 4);
    memcpy(&bx, vb, 4); memcpy(&by, vb + 4, 4);
    memcpy(&cx, vc, 4); memcpy(&cy, vc + 4, 4);
    if (!isfinite(ax) || !isfinite(ay) || !isfinite(bx) || !isfinite(by) || !isfinite(cx) || !isfinite(cy)) return;
    float area = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
    if (area < 0) area = -area;
    /* Glyphs and item-label text are far below this threshold. */
    if (!isfinite(area) || area < 512.0f) return;
    float left = ax, right = ax, top = ay, bottom = ay;
    if (bx < left) left = bx;
    if (cx < left) left = cx;
    if (bx > right) right = bx;
    if (cx > right) right = cx;
    if (by < top) top = by;
    if (cy < top) top = cy;
    if (by > bottom) bottom = by;
    if (cy > bottom) bottom = cy;
    left -= display_x; right -= display_x; top -= display_y; bottom -= display_y;
    if (right <= 0 || bottom <= 0 || left >= width || top >= height) return;
    if (left < 0) left = 0;
    if (top < 0) top = 0;
    if (right > width) right = (float)width;
    if (bottom > height) bottom = (float)height;
    int x = (int)left, y = (int)top;
    int w = (int)(right - left + 1), h = (int)(bottom - top + 1);
    if (w <= 0 || h <= 0 || w > 65535 || h > 65535) return;
    XRectangle rect = { (short)x, (short)y, (unsigned short)w, (unsigned short)h };
    XUnionRectWithRegion(&rect, region, region);
}

static void draw_frame(Display *d, Window window, const unsigned char *p, size_t bytes, int width, int height, GLuint font, struct native_textures *textures, int input_mode) {
    const unsigned char *end=p+bytes; Region input_region=XCreateRegion(); int valid=0;
    if(!input_region)return;
    if(bytes<32 || u32(&p)!=FRAME_MAGIC)goto cleanup;
    uint32_t totalv=u32(&p),totali=u32(&p),lists=u32(&p); float display_x=f32(&p),display_y=f32(&p),display_w=f32(&p),display_h=f32(&p);
    if(lists>1048576u||!isfinite(display_x)||!isfinite(display_y)||!isfinite(display_w)||!isfinite(display_h)||display_w<=0||display_h<=0)goto cleanup;
    uint64_t seen_v=0,seen_i=0;
    glViewport(0,0,width,height); glClearColor(0,0,0,0); glClear(GL_COLOR_BUFFER_BIT);
    /* ImGui vertices already use Wine's physical overlay coordinates.  Do
       not scale them a second time; only account for DisplayPos in clipping. */
    glMatrixMode(GL_PROJECTION); glLoadIdentity(); glOrtho(0,width,height,0,-1,1); glMatrixMode(GL_MODELVIEW); glLoadIdentity();
    glEnable(GL_BLEND); glBlendFunc(GL_SRC_ALPHA,GL_ONE_MINUS_SRC_ALPHA); glEnable(GL_TEXTURE_2D); glBindTexture(GL_TEXTURE_2D,font);
    for(uint32_t li=0;li<lists;li++) {
        if((size_t)(end-p)<12) goto cleanup;
        uint32_t nv=u32(&p), ni=u32(&p), nc=u32(&p);
        size_t vb,ib;if(!multiply_size(nv,20u,&vb)||!multiply_size(ni,2u,&ib)||nc>((size_t)(end-p)/36u))goto cleanup;
        if(vb>SIZE_MAX-ib||(size_t)(end-p)<vb+ib)goto cleanup;
        seen_v+=nv;seen_i+=ni;if(seen_v>totalv||seen_i>totali)goto cleanup;
        const unsigned char *verts=p; p+=vb; const uint16_t *idx=(const uint16_t*)p; p+=ib;
        for(uint32_t ci=0;ci<nc;ci++) {
            if((size_t)(end-p)<36) goto cleanup;
            uint32_t elems=u32(&p), offset=u32(&p), voffset=u32(&p); float x1=f32(&p),y1=f32(&p),x2=f32(&p),y2=f32(&p); uint64_t texture_id=u64(&p);
            if(offset>ni||elems>ni-offset||voffset>nv||!isfinite(x1)||!isfinite(y1)||!isfinite(x2)||!isfinite(y2)||x2<x1||y2<y1||x1-display_x<INT_MIN||x2-display_x>INT_MAX||y1-display_y<INT_MIN||y2-display_y>INT_MAX)goto cleanup;
            for(uint32_t i=0;i<elems;i++)if((uint32_t)idx[offset+i]>=nv-voffset)goto cleanup;
            struct native_texture *texture=texture_id>1?find_texture(textures,texture_id):NULL;
            if(texture_id>1&&!texture)continue;
            for (uint32_t i = 0; i + 2 < elems; i += 3) {
                add_input_triangle(input_region, verts, nv,
                    (uint32_t)idx[offset+i] + voffset, (uint32_t)idx[offset+i+1] + voffset,
                    (uint32_t)idx[offset+i+2] + voffset, display_x, display_y, width, height);
            }
            float sx1=x1-display_x,sy1=y1-display_y,sx2=x2-display_x,sy2=y2-display_y;
            if(sx1<0)sx1=0;
            if(sy1<0)sy1=0;
            if(sx2>width)sx2=(float)width;
            if(sy2>height)sy2=(float)height;
            if(sx2<=sx1||sy2<=sy1)continue;
            glEnable(GL_SCISSOR_TEST); glScissor((int)sx1,height-(int)sy2,(int)(sx2-sx1),(int)(sy2-sy1));
            if(texture_id==1) { glEnable(GL_TEXTURE_2D); glBindTexture(GL_TEXTURE_2D,font); }
            else if(texture) { glEnable(GL_TEXTURE_2D); glBindTexture(GL_TEXTURE_2D,texture->gl); }
            else glDisable(GL_TEXTURE_2D);
            glBegin(GL_TRIANGLES);
            for(uint32_t i=0;i<elems;i++) { uint32_t n=(uint32_t)idx[offset+i]+voffset; if(n>=nv)continue; const unsigned char *v=verts+n*20; float px,py,ux,uy; memcpy(&px,v,4);memcpy(&py,v+4,4);memcpy(&ux,v+8,4);memcpy(&uy,v+12,4); glColor4ub(v[16],v[17],v[18],v[19]); glTexCoord2f(ux,uy); glVertex2f(px,py); }
            glEnd();
        }
    }
    if(seen_v!=totalv||seen_i!=totali||p!=end)goto cleanup;
    /* An empty region is deliberate: events outside menu background pixels go
       directly to PoE.  This replaces the unreliable GetAsyncKeyState polling
       path with complete ButtonPress/ButtonRelease pairs. */
    if(input_mode) XShapeCombineRegion(d, window, ShapeInput, 0, 0, input_region, ShapeSet);
    else XShapeCombineRectangles(d,window,ShapeInput,0,0,NULL,0,ShapeSet,Unsorted);
    valid=1;
cleanup:
    if(!valid) XShapeCombineRectangles(d,window,ShapeInput,0,0,NULL,0,ShapeSet,Unsorted);
    XDestroyRegion(input_region); XFlush(d);
    if(!valid)return;
    glDisable(GL_SCISSOR_TEST); glXSwapBuffers(glXGetCurrentDisplay(),glXGetCurrentDrawable()); (void)totalv;
}
int main(int argc,char **argv) {
    unsigned char auth_token[AUTH_TOKEN_BYTES]; if(argc!=9){fprintf(stderr,"usage: %s x y width height seconds heartbeat port token\n",argv[0]);return 2;} int x=atoi(argv[1]),y=atoi(argv[2]),width=atoi(argv[3]),height=atoi(argv[4]),port=atoi(argv[7]); double duration=0; if(!valid_geometry(x,y,width,height)||!parse_duration(argv[5],&duration)||port<=0||!decode_token(argv[8],auth_token))return 2;
    setlocale(LC_CTYPE,""); Display*d=XOpenDisplay(NULL); if(!d){fputs("gpu: XOpenDisplay failed\n",stderr);return 3;} int screen=DefaultScreen(d); poe_geometry(d,screen,&x,&y,&width,&height); int a[]={GLX_X_RENDERABLE,True,GLX_DRAWABLE_TYPE,GLX_WINDOW_BIT,GLX_RENDER_TYPE,GLX_RGBA_BIT,GLX_X_VISUAL_TYPE,GLX_TRUE_COLOR,GLX_RED_SIZE,8,GLX_GREEN_SIZE,8,GLX_BLUE_SIZE,8,GLX_ALPHA_SIZE,8,GLX_DOUBLEBUFFER,True,None};int count;GLXFBConfig*cfgs=glXChooseFBConfig(d,screen,a,&count);XVisualInfo*vi=NULL;GLXFBConfig cfg=NULL;for(int i=0;i<count;i++){XVisualInfo*c=glXGetVisualFromFBConfig(d,cfgs[i]);XRenderPictFormat*f=c?XRenderFindVisualFormat(d,c->visual):NULL;if(f&&f->direct.alphaMask){vi=c;cfg=cfgs[i];break;}if(c)XFree(c);}if(!vi){fputs("gpu: ARGB visual unavailable\n",stderr);return 4;}
    /* A managed _NET_WM_WINDOW_TYPE_DOCK can be placed below an XWayland
       borderless-fullscreen client by KWin.  This renderer is a transient,
       process-owned visual surface, so keep it override-redirect and maintain
       direct X stacking instead of asking the window manager for a layer. */
    XSetWindowAttributes wa;memset(&wa,0,sizeof wa);wa.colormap=XCreateColormap(d,RootWindow(d,screen),vi->visual,AllocNone);wa.border_pixel=wa.background_pixel=0;wa.override_redirect=True;Window w=XCreateWindow(d,RootWindow(d,screen),x,y,width,height,0,vi->depth,InputOutput,vi->visual,CWColormap|CWBorderPixel|CWBackPixel|CWOverrideRedirect,&wa);XSelectInput(d,w,ButtonPressMask|ButtonReleaseMask|PointerMotionMask|KeyPressMask|KeyReleaseMask);XStoreName(d,w,"GameHelper2 GPU compositor");
    /* This is an input-capable overlay, never an application window.  Without
       the ICCCM Input=False hint KWin can transiently activate it on a click;
       GameHelper2 then observes PoE2 as unfocused and fades its UI. The hint
       prevents focus acquisition while X Shape still routes mouse events. */
    XWMHints hints; memset(&hints,0,sizeof hints); hints.flags=InputHint; hints.input=False; XSetWMHints(d,w,&hints);
    KeyCode f12_keycode=XKeysymToKeycode(d,XK_F12);
    if(f12_keycode!=0) {
        int (*previous_error_handler)(Display*,XErrorEvent*)=XSetErrorHandler(record_passive_grab_error);
        passive_grab_error=0;
        XGrabKey(d,f12_keycode,AnyModifier,RootWindow(d,screen),False,GrabModeAsync,GrabModeAsync);
        XSync(d,False);
        int grab_error=passive_grab_error;
        if(grab_error) { XUngrabKey(d,f12_keycode,AnyModifier,RootWindow(d,screen)); XSync(d,False); }
        XSetErrorHandler(previous_error_handler);
        if(grab_error==BadAccess) { fputs("gpu: F12 passive grab unavailable\n",stderr); f12_keycode=0; }
        else if(grab_error) { fputs("gpu: F12 passive grab failed\n",stderr); f12_keycode=0; }
    }
    int se,er;if(XShapeQueryExtension(d,&se,&er))set_input(d,w,width,height,0);
    GLXContext ctx=glXCreateNewContext(d,cfg,GLX_RGBA_TYPE,NULL,True);if(!ctx||!glXMakeCurrent(d,w,ctx)){fputs("gpu: GLX failed\n",stderr);return 5;}trace_renderer();GLuint font;glGenTextures(1,&font);glBindTexture(GL_TEXTURE_2D,font);glTexParameteri(GL_TEXTURE_2D,GL_TEXTURE_MIN_FILTER,GL_LINEAR);glTexParameteri(GL_TEXTURE_2D,GL_TEXTURE_MAG_FILTER,GL_LINEAR);glPixelStorei(GL_UNPACK_ALIGNMENT,1);
    int listener=socket(AF_INET,SOCK_STREAM,0), client=-1,yes=1;setsockopt(listener,SOL_SOCKET,SO_REUSEADDR,&yes,sizeof yes);fcntl(listener,F_SETFL,fcntl(listener,F_GETFL,0)|O_NONBLOCK);struct sockaddr_in addr;memset(&addr,0,sizeof addr);addr.sin_family=AF_INET;addr.sin_addr.s_addr=htonl(INADDR_LOOPBACK);addr.sin_port=htons(port);if(bind(listener,(struct sockaddr*)&addr,sizeof addr)||listen(listener,1)){perror("gpu bind");return 6;}double started=now_seconds();
    int input_mode=0,shutdown_requested=0; struct keyboard_capture keyboard={0}; struct native_textures textures={0}; struct native_texture_upload texture_upload={0}; double last_valid_heartbeat=wall_seconds(),last_client_seen=0;
    while(!shutdown_requested&&(duration==0||now_seconds()-started<duration)) {
        double loop_now=now_seconds();if(client>=0)last_client_seen=loop_now;if(client<0&&input_mode){input_mode=0;set_input(d,w,width,height,0);}int reconnect_grace=last_client_seen>0&&loop_now-last_client_seen<=AUTHENTICATED_RECONNECT_GRACE_SECONDS;
        double heartbeat_now=wall_seconds();
        double heartbeat_timestamp=heartbeat_timestamp_seconds(argv[6]);
        if(heartbeat_timestamp<0) { trace_renderer_event("explicit-stop",0,client); break; }
        if(heartbeat_timestamp>0) {
            if(heartbeat_now-heartbeat_timestamp>3 && client<0 && !reconnect_grace) { trace_renderer_event("stale-heartbeat",heartbeat_now-heartbeat_timestamp,client); break; }
            last_valid_heartbeat=heartbeat_now;
        } else if(heartbeat_now-last_valid_heartbeat>3 && client<0 && !reconnect_grace) { trace_renderer_event("missing-heartbeat",heartbeat_now-last_valid_heartbeat,client); break; }
        int heartbeat_x=x,heartbeat_y=y,heartbeat_width=width,heartbeat_height=height;
        (void)heartbeat_state(argv[6],&heartbeat_x,&heartbeat_y,&heartbeat_width,&heartbeat_height);
        if(!valid_geometry(heartbeat_x,heartbeat_y,heartbeat_width,heartbeat_height)){heartbeat_x=x;heartbeat_y=y;heartbeat_width=width;heartbeat_height=height;}
        if(!poe_geometry(d,screen,&x,&y,&width,&height)) { x=heartbeat_x;y=heartbeat_y;width=heartbeat_width;height=heartbeat_height; }
        if(valid_geometry(x,y,width,height)) { XMoveResizeWindow(d,w,x,y,(unsigned)width,(unsigned)height); XRaiseWindow(d,w); }
        if(keyboard.requested&&!keyboard.active) request_keyboard(d,w,client,&keyboard,1);
        send_pointer_position(d,w,client);
        while(XPending(d)) {
            XEvent e; XNextEvent(d,&e);
            if(e.type==ButtonPress&&e.xbutton.window==w) { keyboard.user_time=e.xbutton.time; keyboard.suspended=0; }
            else if(e.type==KeyPress) keyboard.user_time=e.xkey.time;
            if(e.type==FocusOut&&e.xfocus.window==keyboard.focus_window&&keyboard.active) {
                Window focused; int revert;
                XGetInputFocus(d,&focused,&revert);
                if(focused!=keyboard.focus_window) {
                    /* A user app switch must release capture even if ImGui
                     * still has an active field. Do not reclaim focus on the
                     * next retry: wait for a new overlay click or text session.
                     * NotifyWhileGrabbed is a real focus loss here too. */
                    release_keyboard(d,client,&keyboard);
                    keyboard.suspended=1;
                }
            }
            if(e.type==ClientMessage&&e.xclient.window==keyboard.focus_window&&
               e.xclient.message_type==keyboard.protocols&&e.xclient.format==32&&
               (Atom)e.xclient.data.l[0]==keyboard.delete_window) {
                /* Closing the focus window must not let the WM kill the
                 * entire compositor connection (the default for Alt+F4). */
                release_keyboard(d,client,&keyboard);
                keyboard.suspended=1;
            }
            if(client>=0&&e.type==MotionNotify) send_mouse(client,-1,0,e.xmotion.x,e.xmotion.y);
            else if(client>=0&&(e.type==KeyPress||e.type==KeyRelease)) {
                char text[16]; KeySym key=NoSymbol; int down=e.type==KeyPress;
                int chars=down?XLookupString(&e.xkey,text,sizeof text,&key,NULL):0;
                if(!down) key=XLookupKeysym(&e.xkey,0);
                if(keyboard.active&&e.xkey.keycode<sizeof keyboard.down) keyboard.down[e.xkey.keycode]=(unsigned char)down;
                send_key(client,key,down,down?utf8_codepoint(text,chars):0);
            } else if(client>=0&&e.type==ButtonPress&&(e.xbutton.button>=4&&e.xbutton.button<=7))
                send_mouse(client,-2-(int)(e.xbutton.button-4),1,e.xbutton.x,e.xbutton.y);
            else if(client>=0&&(e.type==ButtonPress||e.type==ButtonRelease)&&e.xbutton.button<=3) {
                int button=e.xbutton.button==1?0:e.xbutton.button==3?1:2,down=e.type==ButtonPress;
                trace_input("mouse",button,down); send_mouse(client,button,down,e.xbutton.x,e.xbutton.y);
            }
        }
        if(client<0) { client=accept(listener,NULL,NULL); if(client>=0){struct timeval timeout={.tv_sec=1,.tv_usec=0};fcntl(client,F_SETFL,fcntl(client,F_GETFL,0)&~O_NONBLOCK);setsockopt(client,SOL_SOCKET,SO_RCVTIMEO,&timeout,sizeof timeout);setsockopt(client,SOL_SOCKET,SO_SNDTIMEO,&timeout,sizeof timeout);if(!authenticate_client(client,auth_token)){trace_renderer_event("client-auth-failed",0,client);close_client(d,&client,&keyboard);}else XMapRaised(d,w);} usleep(1000); continue; }
        struct pollfd client_poll={.fd=client,.events=POLLIN,.revents=0};
        int poll_result=poll(&client_poll,1,50);
        if(poll_result<0){if(errno==EINTR)continue;trace_renderer_event("client-poll-error",0,client);close_texture_client(d,&client,&keyboard,&texture_upload);continue;}
        if(poll_result==0)continue;
        if(!(client_poll.revents&POLLIN)){
            if(client_poll.revents&(POLLERR|POLLHUP|POLLNVAL)){trace_renderer_event("client-poll-hup",0,client);close_texture_client(d,&client,&keyboard,&texture_upload);}
            continue;
        }
        uint32_t len; int rr=read_exact(client,&len,4);
        if(rr==0) { trace_renderer_event("client-header-eof",0,client); close_texture_client(d,&client,&keyboard,&texture_upload); continue; }
        if(rr<0) { trace_renderer_event("client-header-timeout",0,client); close_texture_client(d,&client,&keyboard,&texture_upload); continue; }
        if(len<4||len>64*1024*1024) { trace_renderer_event("client-invalid-length",0,client); close_texture_client(d,&client,&keyboard,&texture_upload); continue; }
        unsigned char *buf=malloc(len); if(!buf) break;
        rr=read_exact(client,buf,len);
        if(rr<=0) { trace_renderer_event("client-payload-timeout",0,client); free(buf); close_texture_client(d,&client,&keyboard,&texture_upload); continue; }
        if(rr>0) {
            const unsigned char*p=buf;
            if(len>=4) {
                uint32_t magic=u32(&p);
                if(len==4&&magic==SHUTDOWN_MAGIC) { free(buf); shutdown_requested=1; break; }
                else if(magic==FONT_MAGIC&&len>=16) {
                    uint32_t fw=u32(&p),fh=u32(&p),bl=u32(&p);
                    if(fw>0&&fh>0&&fw<=UINT32_MAX/fh&&fw*fh<=UINT32_MAX/4u&&bl==fw*fh*4u&&bl==(uint32_t)(len-16)) { glBindTexture(GL_TEXTURE_2D,font); glTexImage2D(GL_TEXTURE_2D,0,GL_RGBA,fw,fh,0,GL_RGBA,GL_UNSIGNED_BYTE,p); }
                } else if(magic==TEXTURE_MAGIC&&len>=32) {
                    uint64_t id=u64(&p);uint32_t tw=u32(&p),th=u32(&p),total=u32(&p),offset=u32(&p),chunk=u32(&p);int accepted=0,finished=0;
                    int valid=id>1&&tw>0&&th>0&&tw<=MAX_TEXTURE_WIDTH&&th<=MAX_TEXTURE_HEIGHT&&tw<=UINT32_MAX/th&&tw*th<=UINT32_MAX/4u&&total==tw*th*4u&&total<=MAX_TEXTURE_BYTES&&chunk>0&&chunk<=256u*1024u&&chunk==(uint32_t)(len-32)&&offset<=total&&chunk<=total-offset;
                    if(valid&&offset==0){reset_texture_upload(&texture_upload);texture_upload.pixels=malloc(total);if(texture_upload.pixels){texture_upload.id=id;texture_upload.width=tw;texture_upload.height=th;texture_upload.total=total;}}
                    if(valid&&texture_upload.pixels&&texture_upload.id==id&&texture_upload.width==tw&&texture_upload.height==th&&texture_upload.total==total&&texture_upload.received==offset){memcpy(texture_upload.pixels+offset,p,chunk);texture_upload.received+=chunk;accepted=1;finished=texture_upload.received==total;}
                    if(finished){accepted=upload_texture(&textures,id,tw,th,total,texture_upload.pixels);reset_texture_upload(&texture_upload);if(!send_texture_ack(client,1,accepted,id))close_texture_client(d,&client,&keyboard,&texture_upload);}
                    else if(!accepted){reset_texture_upload(&texture_upload);if(!send_texture_ack(client,1,0,id))close_texture_client(d,&client,&keyboard,&texture_upload);}
                } else if(magic==TEXTURE_DELETE_MAGIC&&len==12) { uint64_t id=u64(&p);int success=id>1;if(success){if(texture_upload.id==id)reset_texture_upload(&texture_upload);(void)delete_texture(&textures,id);}if(!send_texture_ack(client,2,success,id))close_texture_client(d,&client,&keyboard,&texture_upload); }
                else if(len==8&&magic==INPUT_MODE_MAGIC) { input_mode=u32(&p)!=0; set_input(d,w,width,height,input_mode); }
                else if(len==8&&magic==KEYBOARD_MODE_MAGIC) request_keyboard(d,w,client,&keyboard,u32(&p)!=0);
                else draw_frame(d,w,buf,len,width,height,font,&textures,input_mode);
            }
        }
        free(buf);
    }
    close_texture_client(d,&client,&keyboard,&texture_upload);delete_all_textures(&textures);
    if(f12_keycode!=0) XUngrabKey(d,f12_keycode,AnyModifier,RootWindow(d,screen));
    if(keyboard.focus_window) XDestroyWindow(d,keyboard.focus_window);
    close(listener);glDeleteTextures(1,&font);glXMakeCurrent(d,None,NULL);glXDestroyContext(d,ctx);XDestroyWindow(d,w);XFree(vi);XFree(cfgs);XCloseDisplay(d);return 0;
}
