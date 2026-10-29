#pragma once

namespace coop_collision {
static unsigned char* effectActor;
static uint32_t hitMask;
static void* geometryOrig;
static uintptr_t lineContinue,infiniteContinue,curveContinue;
static void Reset(){effectActor=nullptr;hitMask=0;}
static int Result(int a,int b){
 hitMask=(a==1?1u:0u)|(b==1?2u:0u);
 const int result=combine(a,b);
 effectActor=a==result&&a?first:b==result&&b?second:first;
 return result;
}
// Keep custom-ABI calls behind real C++ call boundaries. MSVC's inliner does
// not infer the called native function's volatile XMM/GP clobbers from inline
// assembly; inlining LaserOne kept P1's arguments in XMM0..2 and reused those
// clobbered registers for P2 after the native trig routines returned.
__declspec(noinline) static int RectOne(void* box,void* pos){int r;__asm {
 mov eax,box
 mov ecx,pos
 call rectOrig
 mov r,eax
 }return r;}
static int __cdecl RectBoth(void* box,void* pos){
 if(!second)return RectOne(box,pos);
 auto old=singleton();singleton()=first;
 const int a=actorEligible(first)?RectOne(box,pos):0;
 singleton()=second;const int b=actorEligible(second)?RectOne(box,pos):0;
 singleton()=old;return Result(a,b);
}
__declspec(naked) static void RectHook(){__asm {
 push ecx
 push eax
 call RectBoth
 add esp,8
 ret
}}
__declspec(noinline) static int CircleOne(void* pos,float radius){int r;__asm {
 push radius
 mov eax,pos
 call circleOrig
 mov r,eax
 }return r;}
static int __stdcall CircleBoth(void* pos,float radius){
 if(!second)return CircleOne(pos,radius);
 auto old=singleton();singleton()=first;
 const int a=actorEligible(first)?CircleOne(pos,radius):0;
 singleton()=second;const int b=actorEligible(second)?CircleOne(pos,radius):0;
 singleton()=old;return Result(a,b);
}
__declspec(naked) static void CircleHook(){__asm {
 pop edx
 push eax
 push edx
 jmp CircleBoth
}}
__declspec(noinline) static int LaserOne(void* target,void* pos,float angle,float width,float beamLength){int r;__asm {
 push beamLength
 push width
 push angle
 mov eax,pos
 call target
 mov r,eax
 }return r;}
static int __stdcall LaserBoth(void* pos,float angle,float width,float beamLength){
 if(!second)return LaserOne(laserOrig,pos,angle,width,beamLength);
 auto old=singleton();singleton()=first;
 const int a=actorEligible(first)?LaserOne(laserOrig,pos,angle,width,beamLength):0;
 singleton()=second;const int b=actorEligible(second)?LaserOne(laserOrig,pos,angle,width,beamLength):0;
 singleton()=old;return Result(a,b);
}
__declspec(naked) static void LaserHook(){__asm {
 pop edx
 push eax
 push edx
 jmp LaserBoth
}}
static int __stdcall GeometryBoth(void* pos,float angle,float width,float beamLength){
 if(!second)return LaserOne(geometryOrig,pos,angle,width,beamLength);
 auto old=singleton();singleton()=first;
 const int a=actorEligible(first)?LaserOne(geometryOrig,pos,angle,width,beamLength):0;
 singleton()=second;const int b=actorEligible(second)?LaserOne(geometryOrig,pos,angle,width,beamLength):0;
 singleton()=old;return combine(a,b);
}
__declspec(naked) static void GeometryHook(){__asm {
 pop edx
 push eax
 push edx
 jmp GeometryBoth
}}
// Native callers award one team graze per projectile/laser interval. Point that
// award at the actor who actually grazed; a simultaneous graze uses P1's effect.
__declspec(naked) static void EffectEcx(){__asm {
 pushfd
 mov ecx,effectActor
 test ecx,ecx
 jnz found
 mov ecx,dword ptr ds:[4b4514h]
found:
 popfd
 ret
}}
__declspec(naked) static void EffectEdx(){__asm {
 pushfd
 mov edx,effectActor
 test edx,edx
 jnz found
 mov edx,dword ptr ds:[4b4514h]
found:
 popfd
 ret
}}
static void __stdcall CancelBoth(void* function,void* laser,void* position,void* size,int mode,int flag){
 auto cancel=(void(__thiscall*)(void*,void*,void*,int,int))function;
 const uint32_t hits=hitMask;
 if(second&&hits==3){
  cancel(laser,first+0x97c,size,mode,flag);
  cancel(laser,second+0x97c,size,mode,flag);
 }else cancel(laser,position,size,mode,flag);
}
__declspec(naked) static void LineCancel(){__asm {
 push edi
 push edx
 call CancelBoth
 jmp lineContinue
}}
__declspec(naked) static void InfiniteCancel(){__asm {
 push edi
 push eax
 call CancelBoth
 jmp infiniteContinue
}}
__declspec(naked) static void CurveCancel(){__asm {
 push esi
 push edx
 call CancelBoth
 jmp curveContinue
}}
static void Register(coop_patch::Registry& registry){
 const unsigned char align[]={0x55,0x8b,0xec,0x83,0xe4,0xf8};
 const unsigned char ecx[]={0x8b,0x0d,0x14,0x45,0x4b,0};
 const unsigned char edx[]={0x8b,0x15,0x14,0x45,0x4b,0};
 registry.Hook100a(0x437ce0,align,sizeof(align),(void*)GeometryHook,&geometryOrig);
 const uintptr_t ecxSites[]={0x429a02,0x42cba0};
 const uintptr_t edxSites[]={0x414016,0x41b54c,0x41b5ad,0x429a44,0x42b014,0x42b057,0x42cbe6};
 for(auto address:ecxSites)coop_targeting::RegisterLoad(registry,address,ecx,sizeof(ecx),(void*)EffectEcx);
 for(auto address:edxSites)coop_targeting::RegisterLoad(registry,address,edx,sizeof(edx),(void*)EffectEdx);
 const unsigned char line[]={0x8b,0xcf,0xff,0xd2,0xeb,0x2e};
 const unsigned char infinite[]={0x8b,0xcf,0xff,0xd0,0xeb,0x26};
 const unsigned char curve[]={0x8b,0xce,0xff,0xd2,0xeb,0x2f};
 lineContinue=coop_game::Code(0x429a5e);infiniteContinue=coop_game::Code(0x42b069);curveContinue=coop_game::Code(0x42cbfd);
 registry.Hook100a(0x429a2a,line,sizeof(line),(void*)LineCancel);
 registry.Hook100a(0x42b03d,infinite,sizeof(infinite),(void*)InfiniteCancel);
 registry.Hook100a(0x42cbc8,curve,sizeof(curve),(void*)CurveCancel);
}
static uint32_t StateHash(){return hitMask|(effectActor==first&&effectActor?4u:effectActor==second&&effectActor?8u:0u);}
}
