#pragma once
#include "patch.h"

namespace coop_damage_sources {
static bool capturing;
static bool suppressShots;
static uint32_t bombDamage;
static bool bomb[2][128];
static uint32_t shot[2],nativeBomb[2];
static uint32_t rawShot[2],rawBomb[2];
static uintptr_t sourceResume,directResume,projectileResume,projectileSkip,sourceFlagsResume,sourceSkip;
static void Reset(){memset(bomb,0,sizeof(bomb));shot[0]=shot[1]=nativeBomb[0]=nativeBomb[1]=0;capturing=false;}
static void __cdecl Allocated(uintptr_t source,uintptr_t caller){
 for(int p=0;p<2;p++){
  auto begin=(uintptr_t)coop_characters::actors[p]+0x8988;
  if(coop_characters::actors[p]&&source>=begin&&source<begin+128*0x74&&(source-begin)%0x74==0){
   // All six callers were inspected in both retail binaries. The respawn
   // wave and Reimu A's asynchronous bomb beam also retain native damage.
   bomb[p][(source-begin)/0x74]=caller==coop_game::Code(0x407ffa)||caller==coop_game::Code(0x408095)||
    caller==coop_game::Code(0x408fcd)||caller==coop_game::Code(0x436ee1)||caller==coop_game::Code(0x46983c);
  }
 }
}
__declspec(naked) static void AllocationHook(){__asm {
 pushfd
 pushad
 push dword ptr [esp+44]
 push esi
 call Allocated
 add esp,8
 popad
 popfd
 mov eax,esi
 pop esi
 pop ebp
 ret 20
}}
static void __cdecl SourceHit(uintptr_t source,uint32_t amount){
 if(!capturing)return;
 for(int p=0;p<2;p++){
  auto begin=(uintptr_t)coop_characters::actors[p]+0x8988;
  if(coop_characters::actors[p]&&source>=begin&&source<begin+128*0x74&&(source-begin)%0x74==0&&bomb[p][(source-begin)/0x74])bombDamage+=amount;
 }
}
static uint32_t __cdecl TestSource(uintptr_t source){
 for(int p=0;p<2;p++){
  auto begin=(uintptr_t)coop_characters::actors[p]+0x8988;
  if(coop_characters::actors[p]&&source>=begin&&source<begin+128*0x74&&(source-begin)%0x74==0)
   return bomb[p][(source-begin)/0x74];
 }
 return false;
}
// Enemy construction invokes on_tick before assigning an id/linking the
// object. Shots are tested only in the manager's closed collision phase;
// construction still executes native direct/source bombs without consuming shots.
__declspec(naked) static void ProjectileGate(){__asm {
 cmp suppressShots,0
 je native
 mov dword ptr [esp+1ch],1
 jmp projectileSkip
native:
 mov eax,[edi+30h]
 test eax,eax
 jmp projectileResume
}}
__declspec(naked) static void SourceGate(){__asm {
 cmp suppressShots,0
 je native
 pushfd
 pushad
 mov ebp,esp
 and esp,0fffffff0h
 sub esp,512
 fxsave [esp]
 fninit
 fldcw word ptr [esp]
 lea eax,[edi-50h]
 push eax
 call TestSource
 add esp,4
 test eax,eax
 jz skipSource
 fxrstor [esp]
 mov esp,ebp
 popad
 popfd
native:
 mov ecx,[edi+20h]
 test cl,1
 jmp sourceFlagsResume
skipSource:
 fxrstor [esp]
 mov esp,ebp
 popad
 popfd
 jmp sourceSkip
}}
__declspec(naked) static void SourceHook(){__asm {
 pushfd
 pushad
 mov ebp,esp
 and esp,0fffffff0h
 sub esp,512
 fxsave [esp]
 fninit
 fldcw word ptr [esp]
 mov eax,[edi+10h]
 push eax
 lea eax,[edi-50h]
 push eax
 call SourceHit
 add esp,8
 fxrstor [esp]
 mov esp,ebp
 popad
 popfd
 mov eax,[edi+10h]
 add [esp+10h],eax
 jmp sourceResume
}}
__declspec(naked) static void DirectHook(){__asm {
 cmp capturing,0
 je native
 add bombDamage,eax
native:
 fld qword ptr ds:[4a3cf8h]
 add ebx,eax
 jmp directResume
}}
static void Register(coop_patch::Registry& r){
 const unsigned char alloc[]={0x8b,0xc6,0x5e,0x5d,0xc2,0x14,0};
 const unsigned char hit[]={0x8b,0x47,0x10,0x01,0x44,0x24,0x10};
 const unsigned char direct[]={0xdd,0x05,0xf8,0x3c,0x4a,0,0x03,0xd8};
 const unsigned char projectile[]={0x8b,0x47,0x30,0x85,0xc0};
 const unsigned char sourceFlags[]={0x8b,0x4f,0x20,0xf6,0xc1,0x01};
 sourceResume=coop_game::Code(0x43a3b8);directResume=coop_game::Code(0x43a1e6);
 projectileResume=coop_game::Code(0x439f55);projectileSkip=coop_game::Code(0x43a1b7);
 sourceFlagsResume=coop_game::Code(0x43a202);sourceSkip=coop_game::Code(0x43a3d6);
 r.Hook100a(0x43918b,alloc,sizeof(alloc),(void*)AllocationHook);
 r.Hook100a(0x43a3b1,hit,sizeof(hit),(void*)SourceHook);
 r.Hook100a(0x43a1de,direct,sizeof(direct),(void*)DirectHook);
 r.Hook100a(0x439f50,projectile,sizeof(projectile),(void*)ProjectileGate);
 r.Hook100a(0x43a1fc,sourceFlags,sizeof(sourceFlags),(void*)SourceGate);
}
}
