#pragma once
#include "patch.h"

namespace coop_progression {
using Eligible=bool (*)(unsigned char*);
static Eligible actorEligible;
static void* transitionOriginal;
static void* menuTickOriginal;
static bool cooperativeAccess=true;
static unsigned char menuAccessView[0x1edfc];
static unsigned char* menuScore;
static uintptr_t lowerContinue,upperContinue,markerContinue,spellContinue;
static unsigned char *lowerActor,*markerActor,*spellActor;
// Only the two position floats are read from this non-occluding fallback.
static unsigned char clearPosition[0x984];
static void RefreshMenuAccess(){
 auto score=::mem<unsigned char*>(0x4b451c);
 menuScore=score;
 if(!cooperativeAccess||!score)return;
 memcpy(menuAccessView,score,sizeof(menuAccessView));
 // Menu-query view only. Never change the actual clear flags or scorefile.
 for(size_t shot=0;shot<6;shot++){
  menuAccessView[0x1e9d0+shot]|=0x10; // Extra access for each team shot type.
  for(size_t rank=0;rank<4;rank++)for(size_t stage=0;stage<6;stage++)
   menuAccessView[shot*0x45f4+rank*48+stage*8+0x5b1]=1;
 }
 menuScore=menuAccessView;
}
static int __stdcall MenuTick(void* menu){
 RefreshMenuAccess();
 int result=((int(__stdcall*)(void*))menuTickOriginal)(menu);
 coop_characters::MenuFinished((uintptr_t)menu);
 return result;
}
static bool Live(unsigned char* p){
 if(!p)return false;
 int state=::mem<int>((uintptr_t)p+0xa28);
 return (state==0||state==1)&&(!actorEligible||actorEligible(p));
}
static unsigned char* Clear(){
 ::mem<float>((uintptr_t)clearPosition+0x97c)=192.f;
 ::mem<float>((uintptr_t)clearPosition+0x980)=224.f;
 return clearPosition;
}
static void __cdecl TransitionBoth(){
 auto fn=(void(__cdecl*)())transitionOriginal;
 auto original=::singleton();
 fn();
 auto partner=original==::first?::second:original==::second?::first:nullptr;
 if(partner){::singleton()=partner;fn();}
 ::singleton()=original;
}
static bool LowerOverlap(unsigned char* p,bool faded){
 if(!Live(p))return false;
 float x=::mem<float>((uintptr_t)p+0x97c),y=::mem<float>((uintptr_t)p+0x980);
 return faded?(y>=400.f&&x<=-64.f):(y>416.f&&x<-64.f);
}
static void __cdecl SelectLower(unsigned char* gui){
 lowerActor=::singleton();if(!::second)return;
 bool faded=(::mem<uint32_t>((uintptr_t)gui+0x6d18)&1)!=0;
 lowerActor=LowerOverlap(::first,faded)?::first:LowerOverlap(::second,faded)?::second:Clear();
}
static bool UpperOverlap(unsigned char* p,bool faded){
 if(!Live(p))return false;
 float x=::mem<float>((uintptr_t)p+0x97c),y=::mem<float>((uintptr_t)p+0x980);
 return faded?(y<80.f&&x<=0.f):(y<=64.f&&x<-64.f);
}
static unsigned char* __cdecl SelectUpper(unsigned char* gui){
 if(!::second)return ::singleton();
 bool faded=(::mem<uint32_t>((uintptr_t)gui+0x6d18)&8)!=0;
 return UpperOverlap(::first,faded)?::first:UpperOverlap(::second,faded)?::second:Clear();
}
static void __cdecl SelectMarker(unsigned char* enemy){
 markerActor=::singleton();if(!::second)return;
 bool a=Live(::first),b=Live(::second);
 if(!a&&!b){
  markerActor=Clear();
  ::mem<float>((uintptr_t)markerActor+0x97c)=::mem<float>((uintptr_t)enemy+0x1074)+64.f;
 }else if(!a)markerActor=::second;
 else if(!b)markerActor=::first;
 else{
  float x=::mem<float>((uintptr_t)enemy+0x1074);
  float ax=fabsf(x-::mem<float>((uintptr_t)::first+0x97c));
  float bx=fabsf(x-::mem<float>((uintptr_t)::second+0x97c));
  markerActor=bx<ax ? ::second : ::first;
 }
}
static void __cdecl SelectSpell(){
 spellActor=::singleton();if(!::second)return;
 bool a=Live(::first),b=Live(::second);
 if(!a&&!b)spellActor=Clear();
 else if(!a)spellActor=::second;
 else if(!b)spellActor=::first;
 else spellActor=::mem<float>((uintptr_t)::second+0x980)<::mem<float>((uintptr_t)::first+0x980) ? ::second : ::first;
}
// These callouts precede the first FLD of each occlusion calculation. Calling
// C++ after the singleton-load instruction would corrupt a live x87 stack.
__declspec(naked) static void LowerHook(){__asm {
 pushfd
 pushad
 push edi
 call SelectLower
 add esp,4
 popad
 popfd
 test byte ptr [edi+6d18h],1
 jmp lowerContinue
 }}
__declspec(naked) static void UpperHook(){__asm {
 pushfd
 pushad
 push edi
 call SelectUpper
 add esp,4
 mov dword ptr [esp+20],eax
 popad
 popfd
 test byte ptr [edi+6d18h],8
 jmp upperContinue
 }}
__declspec(naked) static void MarkerHook(){__asm {
 pushfd
 pushad
 push ebx
 call SelectMarker
 add esp,4
 popad
 popfd
 fld dword ptr [ebx+1074h]
 jmp markerContinue
 }}
__declspec(naked) static void SpellHook(){__asm {
 pushfd
 pushad
 call SelectSpell
 popad
 popfd
 test byte ptr [edi+7ch],4
 push ebx
 push ebp
 jmp spellContinue
 }}
static void PointerLoad(coop_patch::Registry& registry,uintptr_t address,unsigned char reg,unsigned char** pointer){
 const unsigned char expected[]={0x8b,reg,0x14,0x45,0x4b,0x00};
 unsigned char replacement[]={0x8b,reg,0,0,0,0};
 const uint32_t value=(uint32_t)(uintptr_t)pointer;memcpy(replacement+2,&value,4);
 registry.Bytes100a(address,expected,replacement,sizeof(expected));
}
static void AccessQuery(coop_patch::Registry& registry,uintptr_t address,unsigned char opcode,unsigned char reg=0){
 unsigned char expected[6]={opcode,reg,0,0,0,0},replacement[6]={opcode,reg,0,0,0,0};
 const size_t offset=opcode==0xa1?1:2,count=offset+4;
 const uint32_t source=0x4b451c,target=(uint32_t)(uintptr_t)&menuScore;
 memcpy(expected+offset,&source,4);memcpy(replacement+offset,&target,4);
 registry.Bytes100a(address,expected,replacement,count);
}
static void Register(coop_patch::Registry& registry,Eligible eligible=nullptr){
 actorEligible=eligible;
 RefreshMenuAccess();
 const unsigned char transition[]={0x53,0x55,0x8b,0x2d,0x14,0x45,0x4b,0x00};
 const unsigned char lower[]={0xf6,0x87,0x18,0x6d,0x00,0x00,0x01};
 const unsigned char upper[]={0xf6,0x87,0x18,0x6d,0x00,0x00,0x08};
 const unsigned char marker[]={0xd9,0x83,0x74,0x10,0x00,0x00};
 const unsigned char menuTick[]={0x55,0x8b,0x6c,0x24,0x08,0x56};
 const unsigned char spell[]={0xf6,0x47,0x7c,0x04,0x53,0x55};
 registry.Hook100a(0x41d000,transition,sizeof(transition),(void*)TransitionBoth,&transitionOriginal);
 lowerContinue=coop_game::Code(0x41e158);upperContinue=coop_game::Code(0x41e4b4);markerContinue=coop_game::Code(0x41ee21);
 registry.Hook100a(0x41e151,lower,sizeof(lower),(void*)LowerHook);
 registry.Hook100a(0x41e4ad,upper,sizeof(upper),(void*)UpperHook);
 registry.Hook100a(0x41ee1b,marker,sizeof(marker),(void*)MarkerHook);
 // These loads affect GUI-local registers only; global aiming stays intact.
 for(auto address:{0x41e15eu,0x41e1d2u,0x41e241u,0x41e413u})PointerLoad(registry,address,0x15,&lowerActor);
 PointerLoad(registry,0x41ee27,0x0d,&markerActor);
 spellContinue=coop_game::Code(0x40dbaf);
 registry.Hook100a(0x40dba9,spell,sizeof(spell),(void*)SpellHook);
 PointerLoad(registry,0x40dbb7,0x15,&spellActor);
 PointerLoad(registry,0x40dbf3,0x0d,&spellActor);
 registry.Hook100a(0x43f740,menuTick,sizeof(menuTick),(void*)MenuTick,&menuTickOriginal);
 // Title initialization uses this separate helper to put Extra (index1) in
 // the cursor's disabled list. The four later title queries only style ANMs.
 // Its sole direct caller is 1.00a43FDE2 / 1.00b43FE02, inside MainMenu tick.
 AccessQuery(registry,0x43f160,0xa1);
 // These exact queries control title Extra styling, all three Extra
 // characters and both shots, and practice confirmation/label visibility.
 // Record displays/writes and gameplay continue using the real singleton.
 for(auto address:{0x440196u,0x440656u,0x440996u,0x440ce6u})AccessQuery(registry,address,0xa1);
 AccessQuery(registry,0x4452e1,0x8b,0x0d);
 AccessQuery(registry,0x445a6a,0x8b,0x0d);
 AccessQuery(registry,0x44610a,0x03,0x0d);
 AccessQuery(registry,0x446410,0x8b,0x0d);
}
}
