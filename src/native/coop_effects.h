#pragma once
#include "coop_resources.h"
#include "lockstep.h"

// Reimu A creates a type4 ANM emitter; its later ticks create type3 beams.
// Keep ownership in each native allocation, not the current global Bomb owner.
// The engine frees these extended payloads through the original VM destructor.
namespace coop_effects {
static uint32_t generation=1;
static void *emitterOriginal,*beamOriginal;
static uintptr_t emitterInitResume,beamInitResume;
template<class T> static T& At(uintptr_t p){return *reinterpret_cast<T*>(p);}
struct AnmListGuard {
 bool locked=(At<uint32_t>(0x4cee78)&0x8000)!=0;
 AnmListGuard(){if(locked){EnterCriticalSection((LPCRITICAL_SECTION)0x4cf1d0);++At<unsigned char>(0x4cf221);}}
 ~AnmListGuard(){if(locked){LeaveCriticalSection((LPCRITICAL_SECTION)0x4cf1d0);--At<unsigned char>(0x4cf221);}}
};
template<class Visitor> static bool Visit(Visitor visitor){
 AnmListGuard guard;
 auto manager=At<uintptr_t>(0x4ce8cc);
 if(manager)for(uint32_t list=0;list<2;list++){
  auto node=At<uintptr_t>(manager+(list?0x8856c0:0x8856b8));uint32_t count=0;
  while(node&&count++<65536){
   auto vm=At<uintptr_t>(node);node=At<uintptr_t>(node+4);if(!vm)continue;
   const auto callback=At<uintptr_t>(vm+0x488);
   const bool emitter=callback==coop_game::Code(0x46a2e0),beam=callback==coop_game::Code(0x469d20);
   if(!emitter&&!beam)continue;
   auto payload=At<uintptr_t>(vm+0x478);if(payload)visitor(vm,payload,emitter);
  }
  if(node){coop_lockstep::Fail(11);return false;}
 }
 return true;
}
static uint32_t SourceIndex(uintptr_t payload,uint32_t owner){
 if(owner<1||owner>2||!coop_resources::enabled)return 0;
 auto actor=(uintptr_t)coop_resources::Actor(owner-1);if(!actor)return 0;
 const uintptr_t source=At<uintptr_t>(payload+0x414),begin=actor+0x8988,end=begin+128*0x74;
 return source>=begin&&source<end&&(source-begin)%0x74==0?(uint32_t)((source-begin)/0x74)+1:0;
}
static void Reset(bool retireDamage=false){
 // Activation keeps the actors allocated: retire their old RA beam sources
 // before invalidating effects. Destruction/creation passes false and never
 // dereferences a source pointer from the preceding actor generation.
 if(retireDamage)Visit([](uintptr_t,uintptr_t p,bool emitter){
  if(emitter||At<uint32_t>(p+0x41c)!=generation)return;
  if(SourceIndex(p,At<uint32_t>(p+0x418)))At<uint32_t>(At<uintptr_t>(p+0x414)+0x70)&=~1u;
 });
 if(++generation==0)++generation;
}
static void __cdecl Latch(unsigned char* payload,uint32_t tail){
 int owner=coop_resources::enabled?coop_resources::Index(coop_resources::Player()):-1;
 At<uint32_t>((uintptr_t)payload+tail)=owner+1;
 At<uint32_t>((uintptr_t)payload+tail+4)=generation;
}
// Both insertion sites follow the final FSTP in the original constructor.
__declspec(naked) static void EmitterInit(){__asm {
 pushfd
 pushad
 push 0x30
 push esi
 call Latch
 add esp,8
 popad
 popfd
 mov eax,dword ptr [esi]
 mov dword ptr [ebx+0x430],eax
 jmp emitterInitResume
}}
__declspec(naked) static void BeamInit(){__asm {
 pushfd
 pushad
 push 0x418
 push ebx
 call Latch
 add esp,8
 popad
 popfd
 mov dword ptr [ebx+0x3f4],-1
 jmp beamInitResume
}}
static int Tick(unsigned char* vm,uint32_t tail,void* original){
 auto payload=At<unsigned char*>((uintptr_t)vm+0x478);
 if(!payload)return 1;
 const uint32_t owner=At<uint32_t>((uintptr_t)payload+tail);
 auto fn=(int(__fastcall*)(unsigned char*,void*))original;
 if(!owner)return fn(vm,nullptr); // Native title/demo effects remain native.
 // A reset invalidates both emitters and already-created beams before either
 // can touch an old Player damage-source pointer or attach to a new actor.
 if(owner>2||At<uint32_t>((uintptr_t)payload+tail+4)!=generation||!coop_resources::enabled)return 1;
 auto actor=coop_resources::Actor(owner-1);if(!actor)return 1;
 auto old=coop_resources::Player();coop_resources::Player()=actor;
 int result=fn(vm,nullptr);
 coop_resources::Player()=old;return result;
}
static int __fastcall EmitterTick(unsigned char* vm,void*){return Tick(vm,0x30,emitterOriginal);}
static int __fastcall BeamTick(unsigned char* vm,void*){return Tick(vm,0x418,beamOriginal);}
static uint64_t StateHash(){
 coop_lockstep::Hash h;h.word(0x52414501);
 uint32_t matched=0;
 if(!Visit([&](uintptr_t vm,uintptr_t p,bool emitter){
   const uint32_t tail=emitter?0x30:0x418,owner=At<uint32_t>(p+tail);
   const bool live=owner>=1&&owner<=2&&At<uint32_t>(p+tail+4)==generation&&coop_resources::enabled;
   if(!live)return; // Retired/visual-only effects cannot affect simulation.
   ++matched;h.word(emitter?4:3);h.word(owner);
   h.words(vm+0x430,12);
   if(emitter){h.words(p,0x18);h.words(p+0x1c,0x14);} // Skip timer speed pointer +18.
   else{
    h.words(p+0x3f4,12);h.words(p+0x404,0x10); // Skip speed +400 and damage pointer +414.
    h.word(SourceIndex(p,owner));
   }
 }))return 0;
 h.word(matched);return h.value;
}
static void Register(coop_patch::Registry& r){
 emitterInitResume=coop_game::Code(0x46a2bb);beamInitResume=coop_game::Code(0x469d10);
 const unsigned char small[]={0x30},larger[]={0x38};
 r.Bytes100a(0x46a234,small,larger,1);r.Bytes100a(0x46a23f,small,larger,1);
 const unsigned char beamSize[]={0x18,0x04,0,0},beamExtended[]={0x20,0x04,0,0};
 r.Bytes100a(0x469738,beamSize,beamExtended,4);r.Bytes100a(0x469758,beamSize,beamExtended,4);
 const unsigned char emitterInit[]={0x8b,0x06,0x89,0x83,0x30,0x04,0,0};
 const unsigned char beamInit[]={0xc7,0x83,0xf4,0x03,0,0,0xff,0xff,0xff,0xff};
 const unsigned char tick[]={0x55,0x8b,0xec,0x83,0xe4,0xf8};
 r.Hook100a(0x46a2b3,emitterInit,sizeof(emitterInit),(void*)EmitterInit);
 r.Hook100a(0x469d06,beamInit,sizeof(beamInit),(void*)BeamInit);
 r.Hook100a(0x46a2e0,tick,sizeof(tick),(void*)EmitterTick,&emitterOriginal);
 r.Hook100a(0x469d20,tick,sizeof(tick),(void*)BeamTick,&beamOriginal);
}
}
