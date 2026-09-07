// <copyright file="wayland-standin.c" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>
#define _GNU_SOURCE
#include <wayland-client.h>
#include "xdg-shell-client-protocol.h"
#include <sys/mman.h>
#include <unistd.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
static struct wl_compositor *comp; static struct wl_shm *shm; static struct xdg_wm_base *wm; static struct wl_surface *surface; static struct wl_buffer *buffer;
static void keymap(void*d,struct wl_keyboard*k,uint32_t f,int32_t fd,uint32_t size){(void)d;(void)k;(void)f;(void)size;close(fd);}
static void enter(void*d,struct wl_keyboard*k,uint32_t ser,struct wl_surface*s,struct wl_array*a){(void)d;(void)k;(void)ser;(void)s;(void)a;puts("KEYBOARD ENTER");fflush(stdout);}
static void leave(void*d,struct wl_keyboard*k,uint32_t ser,struct wl_surface*s){(void)d;(void)k;(void)ser;(void)s;puts("KEYBOARD LEAVE");fflush(stdout);}
static void key(void*d,struct wl_keyboard*k,uint32_t ser,uint32_t time,uint32_t code,uint32_t state){(void)d;(void)k;(void)ser;(void)time;printf("PUBLIC FIXTURE KEY %u %u\n",code,state);fflush(stdout);}
static void mods(void*d,struct wl_keyboard*k,uint32_t ser,uint32_t a,uint32_t b,uint32_t c,uint32_t g){(void)d;(void)k;(void)ser;(void)a;(void)b;(void)c;(void)g;}
static const struct wl_keyboard_listener kl={.keymap=keymap,.enter=enter,.leave=leave,.key=key,.modifiers=mods};
static void caps(void*d,struct wl_seat*s,uint32_t c){(void)d;if(c&WL_SEAT_CAPABILITY_KEYBOARD){struct wl_keyboard*k=wl_seat_get_keyboard(s);wl_keyboard_add_listener(k,&kl,0);}}
static const struct wl_seat_listener seat_listener={.capabilities=caps};
static void ping(void*d,struct xdg_wm_base*w,uint32_t s){(void)d;xdg_wm_base_pong(w,s);}static const struct xdg_wm_base_listener wml={ping};
static void global(void*d,struct wl_registry*r,uint32_t n,const char*i,uint32_t v){(void)d;(void)v;if(!strcmp(i,"wl_seat")){struct wl_seat*seat=wl_registry_bind(r,n,&wl_seat_interface,1);wl_seat_add_listener(seat,&seat_listener,0);}else if(!strcmp(i,"wl_compositor"))comp=wl_registry_bind(r,n,&wl_compositor_interface,1);else if(!strcmp(i,"wl_shm"))shm=wl_registry_bind(r,n,&wl_shm_interface,1);else if(!strcmp(i,"xdg_wm_base")){wm=wl_registry_bind(r,n,&xdg_wm_base_interface,1);xdg_wm_base_add_listener(wm,&wml,0);}}
static void gone(void*d,struct wl_registry*r,uint32_t n){(void)d;(void)r;(void)n;}static const struct wl_registry_listener registry_listener={global,gone};
static void configure(void*d,struct xdg_surface*s,uint32_t serial){(void)d;xdg_surface_ack_configure(s,serial);wl_surface_attach(surface,buffer,0,0);wl_surface_damage(surface,0,0,800,600);wl_surface_commit(surface);puts("WAYLAND STANDIN READY");fflush(stdout);}static const struct xdg_surface_listener sl={configure};
static void top_config(void*d,struct xdg_toplevel*t,int32_t w,int32_t h,struct wl_array*s){(void)d;(void)t;(void)w;(void)h;(void)s;}static void top_close(void*d,struct xdg_toplevel*t){(void)d;(void)t;exit(0);}static const struct xdg_toplevel_listener tl={.configure=top_config,.close=top_close};
int main(){const char*fixture=getenv("GH_TEXT_FIXTURE");if(!fixture||strcmp(fixture,"1"))return 2;struct wl_display*display=wl_display_connect(0);if(!display)return 2;struct wl_registry*r=wl_display_get_registry(display);wl_registry_add_listener(r,&registry_listener,0);wl_display_roundtrip(display);if(!comp||!shm||!wm)return 3;int fd=memfd_create("public-fixture",MFD_CLOEXEC);size_t size=800*600*4;ftruncate(fd,size);uint32_t*p=mmap(0,size,PROT_READ|PROT_WRITE,MAP_SHARED,fd,0);for(size_t i=0;i<size/4;i++)p[i]=0xff224466;struct wl_shm_pool*pool=wl_shm_create_pool(shm,fd,size);buffer=wl_shm_pool_create_buffer(pool,0,800,600,800*4,WL_SHM_FORMAT_XRGB8888);wl_shm_pool_destroy(pool);close(fd);surface=wl_compositor_create_surface(comp);struct xdg_surface*xs=xdg_wm_base_get_xdg_surface(wm,surface);xdg_surface_add_listener(xs,&sl,0);struct xdg_toplevel*top=xdg_surface_get_toplevel(xs);xdg_toplevel_add_listener(top,&tl,0);xdg_toplevel_set_title(top,"Public Wayland game stand-in");xdg_toplevel_set_app_id(top,"org.example.GHFixture");xdg_toplevel_set_fullscreen(top,0);wl_surface_commit(surface);while(wl_display_dispatch(display)>=0){}return 0;}
