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
#define INPUT_MODE_MAGIC 0x31435345u
#define MOUSE_INPUT_MAGIC 0x31494e45u
#define KEY_INPUT_MAGIC 0x314b4e45u
#define KEYBOARD_MODE_MAGIC 0x314b5345u
#define AUTH_MAGIC 0x31485541u
#define READY_MAGIC 0x31594452u
#define AUTH_TOKEN_BYTES 32u
#define MAX_OVERLAY_DIMENSION 32767
#define MIN_OVERLAY_POSITION (-32768)
#define MAX_OVERLAY_POSITION 32767
#define KEYBOARD_RETRY_SECONDS 0.25
static double now_seconds(void) { struct timespec v; clock_gettime(CLOCK_MONOTONIC, &v); return v.tv_sec + v.tv_nsec / 1e9; }
static void trace_input(const char *event, int a, int b) { FILE *f=fopen("/tmp/gamehelper2-gpu-input.log","a"); if(f){fprintf(f,"%.3f %s %d %d\n",now_seconds(),event,a,b);fclose(f);} }
static void trace_renderer(void) {
    const char *vendor=(const char*)glGetString(GL_VENDOR);
    const char *renderer=(const char*)glGetString(GL_RENDERER);
    const char *version=(const char*)glGetString(GL_VERSION);
    FILE *f=fopen("/tmp/gamehelper2-gpu-renderer.log","a");
    if(f){fprintf(f,"%.3f pid=%ld vendor=%s renderer=%s version=%s\n",now_seconds(),(long)getpid(),vendor?vendor:"unknown",renderer?renderer:"unknown",version?version:"unknown");fclose(f);}
}
static uint32_t u32(const unsigned char **p) { uint32_t v; memcpy(&v,*p,4); *p+=4; return v; }
static float f32(const unsigned char **p) { float v; memcpy(&v,*p,4); *p+=4; return v; }
static int multiply_size(size_t a,size_t b,size_t*out){if(a&&b>SIZE_MAX/a)return 0;*out=a*b;return 1;}
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
struct keyboard_capture { int requested; int active; double retry_at; unsigned char down[256]; };
static void release_keyboard(Display *d, int fd, struct keyboard_capture *state) {
    if(state->active) {
        for(size_t i=0;i<sizeof state->down;i++) if(state->down[i])
            send_key(fd,XkbKeycodeToKeysym(d,(KeyCode)i,0,0),0,0);
        XUngrabKeyboard(d,CurrentTime);
        trace_input("keyboard",0,0);
    }
    memset(state->down,0,sizeof state->down);
    state->active=0;
    XFlush(d);
}
static void request_keyboard(Display *d, Window w, int fd, struct keyboard_capture *state, int capture) {
    state->requested=capture;
    if(!capture) { state->retry_at=0; release_keyboard(d,fd,state); return; }
    if(!state->active && now_seconds()>=state->retry_at) {
        int result=XGrabKeyboard(d,w,False,GrabModeAsync,GrabModeAsync,CurrentTime);
        if(result==GrabSuccess) state->active=1;
        else state->retry_at=now_seconds()+KEYBOARD_RETRY_SECONDS;
        trace_input("keyboard",capture,result);
        XFlush(d);
    }
}
static void close_client(Display *d, int *fd, struct keyboard_capture *keyboard) {
    release_keyboard(d,*fd,keyboard);
    keyboard->requested=0;
    keyboard->retry_at=0;
    if(*fd>=0) close(*fd);
    *fd=-1;
}
static int passive_grab_error;
static int record_passive_grab_error(Display *d, XErrorEvent *event) { (void)d; passive_grab_error=event->error_code; return 0; }
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
static int heartbeat_state(const char *path, int *x, int *y, int *width, int *height) { FILE *f=fopen(path,"r"); long stamp=0; int mode=0; if(f){if(fscanf(f,"%ld %d %d %d %d %d",&stamp,&mode,x,y,width,height)!=6)mode=0;fclose(f);} (void)stamp; return mode!=0; }
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

static void draw_frame(Display *d, Window window, const unsigned char *p, size_t bytes, int width, int height, GLuint font) {
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
        size_t vb,ib;if(!multiply_size(nv,20u,&vb)||!multiply_size(ni,2u,&ib)||nc>((size_t)(end-p)/32u))goto cleanup;
        if(vb>SIZE_MAX-ib||(size_t)(end-p)<vb+ib)goto cleanup;
        seen_v+=nv;seen_i+=ni;if(seen_v>totalv||seen_i>totali)goto cleanup;
        const unsigned char *verts=p; p+=vb; const uint16_t *idx=(const uint16_t*)p; p+=ib;
        for(uint32_t ci=0;ci<nc;ci++) {
            if((size_t)(end-p)<32) goto cleanup;
            uint32_t elems=u32(&p), offset=u32(&p), voffset=u32(&p); float x1=f32(&p),y1=f32(&p),x2=f32(&p),y2=f32(&p); uint32_t textured=u32(&p);
            if(offset>ni||elems>ni-offset||voffset>nv||!isfinite(x1)||!isfinite(y1)||!isfinite(x2)||!isfinite(y2)||x2<x1||y2<y1||x1-display_x<INT_MIN||x2-display_x>INT_MAX||y1-display_y<INT_MIN||y2-display_y>INT_MAX)goto cleanup;
            for(uint32_t i=0;i<elems;i++)if((uint32_t)idx[offset+i]>=nv-voffset)goto cleanup;
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
            if(textured==1) { glEnable(GL_TEXTURE_2D); glBindTexture(GL_TEXTURE_2D,font); } else glDisable(GL_TEXTURE_2D);
            glBegin(GL_TRIANGLES);
            for(uint32_t i=0;i<elems;i++) { uint32_t n=(uint32_t)idx[offset+i]+voffset; if(n>=nv)continue; const unsigned char *v=verts+n*20; float px,py,ux,uy; memcpy(&px,v,4);memcpy(&py,v+4,4);memcpy(&ux,v+8,4);memcpy(&uy,v+12,4); if(textured==2) glColor4ub(0,0,0,v[19]); else glColor4ub(v[16],v[17],v[18],v[19]); glTexCoord2f(ux,uy); glVertex2f(px,py); }
            glEnd();
        }
    }
    if(seen_v!=totalv||seen_i!=totali||p!=end)goto cleanup;
    /* An empty region is deliberate: events outside menu background pixels go
       directly to PoE.  This replaces the unreliable GetAsyncKeyState polling
       path with complete ButtonPress/ButtonRelease pairs. */
    XShapeCombineRegion(d, window, ShapeInput, 0, 0, input_region, ShapeSet);
    valid=1;
cleanup:
    if(!valid) XShapeCombineRectangles(d,window,ShapeInput,0,0,NULL,0,ShapeSet,Unsorted);
    XDestroyRegion(input_region); XFlush(d);
    if(!valid)return;
    glDisable(GL_SCISSOR_TEST); glXSwapBuffers(glXGetCurrentDisplay(),glXGetCurrentDrawable()); (void)totalv;
}
int main(int argc,char **argv) {
    unsigned char auth_token[AUTH_TOKEN_BYTES]; if(argc!=9){fprintf(stderr,"usage: %s x y width height seconds heartbeat port token\n",argv[0]);return 2;} int x=atoi(argv[1]),y=atoi(argv[2]),width=atoi(argv[3]),height=atoi(argv[4]),port=atoi(argv[7]); double duration=atof(argv[5]); if(!valid_geometry(x,y,width,height)||duration<=0||port<=0||!decode_token(argv[8],auth_token))return 2;
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
    int input_mode=0; struct keyboard_capture keyboard={0};
    while(now_seconds()-started<duration) {
        struct stat hb;
        if(stat(argv[6],&hb)||now_seconds()-hb.st_mtime>3) break;
        int heartbeat_x=x,heartbeat_y=y,heartbeat_width=width,heartbeat_height=height;
        int requested=heartbeat_state(argv[6],&heartbeat_x,&heartbeat_y,&heartbeat_width,&heartbeat_height);
        if(!valid_geometry(heartbeat_x,heartbeat_y,heartbeat_width,heartbeat_height)){heartbeat_x=x;heartbeat_y=y;heartbeat_width=width;heartbeat_height=height;requested=0;}
        if(!poe_geometry(d,screen,&x,&y,&width,&height)) { x=heartbeat_x;y=heartbeat_y;width=heartbeat_width;height=heartbeat_height; }
        if(valid_geometry(x,y,width,height)) { XMoveResizeWindow(d,w,x,y,(unsigned)width,(unsigned)height); XRaiseWindow(d,w); }
        if(requested!=input_mode) { input_mode=requested; set_input(d,w,width,height,input_mode); }
        if(keyboard.requested&&!keyboard.active) request_keyboard(d,w,client,&keyboard,1);
        send_pointer_position(d,w,client);
        while(XPending(d)) {
            XEvent e; XNextEvent(d,&e);
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
        if(client<0) { client=accept(listener,NULL,NULL); if(client>=0){struct timeval timeout={.tv_sec=1,.tv_usec=0};fcntl(client,F_SETFL,fcntl(client,F_GETFL,0)&~O_NONBLOCK);setsockopt(client,SOL_SOCKET,SO_RCVTIMEO,&timeout,sizeof timeout);setsockopt(client,SOL_SOCKET,SO_SNDTIMEO,&timeout,sizeof timeout);if(!authenticate_client(client,auth_token))close_client(d,&client,&keyboard);else XMapRaised(d,w);} usleep(1000); continue; }
        struct pollfd client_poll={.fd=client,.events=POLLIN,.revents=0};
        int poll_result=poll(&client_poll,1,50);
        if(poll_result<0){if(errno==EINTR)continue;close_client(d,&client,&keyboard);continue;}
        if(poll_result==0)continue;
        if(!(client_poll.revents&POLLIN)){
            if(client_poll.revents&(POLLERR|POLLHUP|POLLNVAL))close_client(d,&client,&keyboard);
            continue;
        }
        uint32_t len; int rr=read_exact(client,&len,4);
        if(rr==0) { close_client(d,&client,&keyboard); continue; }
        if(rr<0) { close_client(d,&client,&keyboard); continue; }
        if(len<4||len>64*1024*1024) { close_client(d,&client,&keyboard); continue; }
        unsigned char *buf=malloc(len); if(!buf) break;
        rr=read_exact(client,buf,len);
        if(rr<=0) { free(buf); close_client(d,&client,&keyboard); continue; }
        if(rr>0) {
            const unsigned char*p=buf;
            if(len>=4) {
                uint32_t magic=u32(&p);
                if(magic==FONT_MAGIC&&len>=16) {
                    uint32_t fw=u32(&p),fh=u32(&p),bl=u32(&p);
                    if(fw>0&&fh>0&&fw<=UINT32_MAX/fh&&fw*fh<=UINT32_MAX/4u&&bl==fw*fh*4u&&bl==(uint32_t)(len-16)) { glBindTexture(GL_TEXTURE_2D,font); glTexImage2D(GL_TEXTURE_2D,0,GL_RGBA,fw,fh,0,GL_RGBA,GL_UNSIGNED_BYTE,p); }
                } else if(len==8&&magic==INPUT_MODE_MAGIC) set_input(d,w,width,height,u32(&p)!=0);
                else if(len==8&&magic==KEYBOARD_MODE_MAGIC) request_keyboard(d,w,client,&keyboard,u32(&p)!=0);
                else draw_frame(d,w,buf,len,width,height,font);
            }
        }
        free(buf);
    }
    close_client(d,&client,&keyboard);
    if(f12_keycode!=0) XUngrabKey(d,f12_keycode,AnyModifier,RootWindow(d,screen));
    close(listener);glDeleteTextures(1,&font);glXMakeCurrent(d,None,NULL);glXDestroyContext(d,ctx);XDestroyWindow(d,w);XFree(vi);XFree(cfgs);XCloseDisplay(d);return 0;
}
