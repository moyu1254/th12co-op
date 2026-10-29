// TH12 1.00a/1.00b cooperative runtime. Hook addresses and both layouts were
// checked against the user's executables. No game assets are included.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <share.h>
#include <string.h>
#include <math.h>
#include <intrin.h>
#include "lockstep.h"
#include "coop_boot.h"
#include "patch.h"

struct Shared {
    uint32_t magic, version;
    volatile uint32_t p1, p2, flags, status, frame;
    volatile float p1x,p1y,p2x,p2y;
    volatile uint32_t heartbeat;
    uint32_t reserved[4];
};
static_assert(sizeof(Shared)==64,"shared memory ABI");
static Shared* ipc;
static HANDLE mapping;
static unsigned char *first,*second,*bombOwner;
static uint32_t previous[2];
static bool creating, cooperativeRun;
static const uintptr_t PLAYER=0x4b4514, GAME_INPUT=0x4d49d0;
static void *createOrig,*destroyOrig,*activateOrig,*damageOrig,*rectOrig,*circleOrig,*laserOrig,*itemOrig,*saveOrig,*optionsOrig,*stageLoadOrig;
static FILE* logfile;
static void log(const char* s) { if(logfile){ fprintf(logfile,"%lu %s\n",GetTickCount(),s); fflush(logfile); } }
template<class T> static T& mem(uintptr_t p){return *reinterpret_cast<T*>(p);}
static unsigned char*& singleton(){return mem<unsigned char*>(PLAYER);}
static int combine(int a,int b){return a==1||b==1?1:a==2||b==2?2:0;}
static uint32_t keys2(){
 uint32_t r=0; struct K{int vk;uint32_t mask;};
 const K keys[]={{'I',16},{'K',32},{'J',64},{'L',128},{'F',1},{'G',2},{'D',8}};
 DWORD pid=0;GetWindowThreadProcessId(GetForegroundWindow(),&pid);if(pid!=GetCurrentProcessId())return 0;
 for(auto k:keys)if(GetAsyncKeyState(k.vk)&0x8000)r|=k.mask;return r;
}
static uint32_t inputFor(int n){
 if(coop_lockstep::Active())return coop_lockstep::InputFor(n)&0xfb;
 if(ipc && (ipc->flags&1)){
  if((uint32_t)(GetTickCount()-ipc->heartbeat)>1000)return 0;
  return (n?ipc->p2:ipc->p1)&0xfb;
 }
 return n?keys2():mem<uint32_t>(GAME_INPUT);
}
static void setInput(int n,uint32_t v){
 uint32_t* p=(uint32_t*)GAME_INPUT;p[0]=v;p[1]=previous[n];p[2]=v;p[3]=v&~previous[n];p[4]=previous[n]&~v;
 previous[n]=v;
}
#include "coop_resources.h"
static bool actorEligible(unsigned char* p){return coop_resources::Eligible(p);}
#include "coop_targeting.h"
#include "coop_collision.h"
#include "coop_items.h"
#include "coop_progression.h"
#include "coop_homing.h"
#include "coop_force.h"
#include "coop_effects.h"
#include "coop_audit_campaign.h"
#include "coop_audit.h"
static uint64_t cooperativeStateHash(){
 coop_lockstep::Hash h;
 const uint64_t resources=coop_resources::OwnStateHash(),items=coop_items::DeterministicStateHash();
 h.word((uint32_t)resources);h.word((uint32_t)(resources>>32));
 h.word((uint32_t)items);h.word((uint32_t)(items>>32));
 const uint64_t effects=coop_effects::StateHash();h.word((uint32_t)effects);h.word((uint32_t)(effects>>32));
 h.word(coop_targeting::StateHash());h.word(coop_collision::StateHash());h.word(coop_homing::StateHash());h.word(coop_force::StateHash());
 for(auto actor:{first,second}){
  if(!actor){h.word(0);continue;}
  auto target=mem<uintptr_t>((uintptr_t)actor+0x8980);
  h.word(mem<unsigned char>((uintptr_t)actor+0x8984));
  // Normalize the homing pointer to an enemy-list index, never an address.
  uint32_t index=0,found=0;auto manager=mem<uintptr_t>(0x4b43dc);
  auto node=manager?mem<uintptr_t>(manager+0x68):0;
  while(node&&index<4096){++index;if(mem<uintptr_t>(node)==target)found=index;node=mem<uintptr_t>(node+4);}
  h.word(found);
 }
 h.word(bombOwner==first&&bombOwner?1u:bombOwner==second&&bombOwner?2u:0u);
 h.word(previous[0]);h.word(previous[1]);return h.value;
}
static void resetCooperativeState(){
 coop_resources::Reset(first,second);coop_targeting::Reset();coop_collision::Reset();coop_items::Reset();coop_homing::Reset();coop_force::Reset();
 coop_effects::Reset();
}
static void publish(){
 if(!ipc)return;
 ipc->status=second?2:1;
 if(first){ipc->p1x=mem<float>((uintptr_t)first+0x97c);ipc->p1y=mem<float>((uintptr_t)first+0x980);}
 if(first)ipc->reserved[0]=mem<uint32_t>((uintptr_t)first+0xa28);
 if(second)ipc->reserved[1]=mem<uint32_t>((uintptr_t)second+0xa28);
 for(int playerIndex=0;playerIndex<2;playerIndex++){
  auto p=playerIndex?second:first;uint32_t count=0;
  if(p)for(int b=0;b<256;b++)if(mem<int>((uintptr_t)p+0xa58+b*0x78+0x48)==1)count++;
  ipc->reserved[2+playerIndex]=count;
 }
 if(second){ipc->p2x=mem<float>((uintptr_t)second+0x97c);ipc->p2y=mem<float>((uintptr_t)second+0x980);}
}
static void* __cdecl createHook(){
 auto create=(void*(__cdecl*)())createOrig;
 auto p=(unsigned char*)create();
 if(!p||creating)return p;
 cooperativeRun=false;
 first=p;
 bombOwner=nullptr;
 // Demos/replays keep the original single input stream.
 auto gm=mem<unsigned char*>(0x4b44e8);
 if((mem<uint32_t>(0x4b0ce0)&0x20)||(gm&&mem<int>((uintptr_t)gm+0x74)))return p;
 creating=true;
 second=(unsigned char*)create();
 creating=false;
 singleton()=first;
 previous[0]=previous[1]=0;
 if(second){
  cooperativeRun=true;
  // The game enables/disables only the Player returned by its allocator. Own
  // both actor passes in that single lifecycle callback instead of registering
  // an independently enabled P2 that would ignore pauses and scene changes.
  auto runner=mem<void*>(0x4ce89c);
  bool threaded=(mem<uint32_t>(0x4cee78)&0x8000)!=0;
  if(threaded){EnterCriticalSection((LPCRITICAL_SECTION)0x4cf0f8);++mem<unsigned char>(0x4cf218);}
  for(size_t offset=8;offset<=12;offset+=4){
   auto node=mem<void*>((uintptr_t)second+offset);
   ((void(__fastcall*)(void*,void*))coop_game::Code(0x462870))(node,runner);
   mem<void*>((uintptr_t)second+offset)=nullptr;
  }
  if(threaded){--mem<unsigned char>(0x4cf218);LeaveCriticalSection((LPCRITICAL_SECTION)0x4cf0f8);}
  mem<float>((uintptr_t)first+0x97c)=-32.f;mem<int>((uintptr_t)first+0x988)=-4096;
  mem<float>((uintptr_t)second+0x97c)=32.f;mem<int>((uintptr_t)second+0x988)=4096;
  log("Two native Player objects created");
 } else log("Second Player allocation failed");
 resetCooperativeState();
 publish();return p;
}
static void __stdcall destroyHook(void* p){
 if(p==first)bombOwner=nullptr;
 if(p==first && second){
  auto p2=second;second=nullptr;
  // Player::destructor owns the shared ANM slot 7. Temporarily remove that
  // ownership while destroying P2's own nodes, shot data and embedded VM buffers.
  auto anm=mem<unsigned char*>(0x4ce8cc);
  auto slot=(uintptr_t)anm+0x4b50dc;
  void* asset=mem<void*>(slot);void* cached=mem<void*>(0x4ce8a8);
  uint32_t mode=mem<uint32_t>(0x4b0ce0);
  mem<void*>(slot)=nullptr;mem<uint32_t>(0x4b0ce0)=mode&~1u;
  ((void(__stdcall*)(void*))destroyOrig)(p2);
  mem<void*>(slot)=asset;mem<void*>(0x4ce8a8)=cached;mem<uint32_t>(0x4b0ce0)=mode;
  ((void(__cdecl*)(void*))coop_game::Code(0x46ca2f))(p2);
  singleton()=first;
  log("Second Player resources released");
 }
 ((void(__stdcall*)(void*))destroyOrig)(p);
 if(p==first){first=nullptr;resetCooperativeState();publish();}
}
static void __cdecl activateHook(){
 auto original=(void(__cdecl*)())activateOrig;
 auto current=singleton();
 original();
 if(current==first&&second){
  singleton()=second;
  original();
  singleton()=current;
  log("Both Player objects activated through native reset");
  coop_resources::OnActivate();coop_targeting::Reset();coop_collision::Reset();coop_items::Reset();
  coop_effects::Reset(true);
 }
 publish();
}
static int __fastcall tickHook(unsigned char* p,void*){
 if(p==first&&coop_audit::Poll())return 1;
 if(p==first&&coop_campaign::Active())coop_campaign::BeforeFrame();
 uint32_t saved[5];memcpy(saved,(void*)GAME_INPUT,sizeof(saved));
 auto old=singleton();singleton()=p;
 int n=p==second?1:0;
 if(second)setInput(n,inputFor(n));
 auto bomb=mem<unsigned char*>(0x4b43c4);
 int before=bomb?mem<int>((uintptr_t)bomb+0x3c):0;
 coop_resources::BeforeTick(p);
 int result=((int(__stdcall*)(void*))coop_game::Code(0x436b80))(p);
 coop_resources::AfterTick(p);
 if(bomb&&before==0&&mem<int>((uintptr_t)bomb+0x3c)==1)bombOwner=p;
 memcpy((void*)GAME_INPUT,saved,sizeof(saved));singleton()=old;
 if(n==0&&ipc)++ipc->frame;
 if(p==first&&second)tickHook(second,nullptr);
 publish();return result;
}
static int __fastcall drawHook(unsigned char* p,void*){
 auto old=singleton();singleton()=p;
 uint32_t color=mem<uint32_t>((uintptr_t)p+0x3d0);
 if(p==second)mem<uint32_t>((uintptr_t)p+0x3d0)=(color&0xff000000)|0x80e8ff;
 int result; uintptr_t target=coop_game::Code(0x4375e0);
 __asm {
 mov eax,p
 call target
 mov result,eax
 }
 mem<uint32_t>((uintptr_t)p+0x3d0)=color;
 singleton()=old;
 if(p==first&&second)drawHook(second,nullptr);
 return result;
}
static int __fastcall bombHook(void* p,void*){
 auto old=singleton();
 if(bombOwner && (bombOwner==first||bombOwner==second))singleton()=bombOwner;
 int result;uintptr_t target=coop_game::Code(0x406cc0);
 __asm {
 mov eax,p
 call target
 mov result,eax
 }
 singleton()=old;return result;
}
static void __stdcall optionsHook(unsigned char* p){
 auto old=singleton();singleton()=p;
 auto fn=(void(__stdcall*)(void*))optionsOrig;
 fn(p);
 auto other=p==first?second:p==second?first:nullptr;
 if(other){singleton()=other;fn(other);}
 singleton()=old;
}
static int __stdcall damageHook(void* position,void* boxSize){
 return coop_campaign::ScaleDamage(coop_resources::Damage(position,boxSize,damageOrig));
}
static void __cdecl chooseItemPlayer(unsigned char* item){coop_items::SelectForMotion(item);}
static uintptr_t itemContinue=0x425c2d;
__declspec(naked) static void itemLoopHook(){__asm {
 pushfd
 pushad
 push edi
 call chooseItemPlayer
 add esp,4
 popad
 popfd
 mov eax,[edi+9b0h]
 jmp itemContinue
 }}
static int __fastcall itemHook(void* p,void* d){
 return coop_items::Tick(p,d,itemOrig,actorEligible);
}
static int __fastcall saveHook(void* filename,void* name,int arg){
 if(cooperativeRun){log("Cooperative replay save declined: native format stores only one input stream");return -1;}
 return ((int(__fastcall*)(void*,void*,int))saveOrig)(filename,name,arg);
}
// Commit input before native edge/repeat derivation, including menus and pause.
static uintptr_t pollContinue=0x4631e8;
static int __cdecl stageLoadHook(){
 int result=((int(__cdecl*)())stageLoadOrig)();
 coop_lockstep::StageLoadStarted();return result;
}
static uintptr_t seedContinue=0x42f568;
static DWORD __cdecl initialSeed(){return coop_lockstep::InitialSeed();}
__declspec(naked) static void seedHook(){
 __asm {
 call initialSeed
 jmp seedContinue
 }
}
static uint32_t __cdecl smokeInput(uint32_t physical){
 if(coop_lockstep::Active())return coop_lockstep::Poll(physical,first,second);
#ifdef COOP_SMOKE_TEST
 if(ipc&&(ipc->flags&2)&&(uint32_t)(GetTickCount()-ipc->heartbeat)<=1000)return ipc->p1;
#endif
 return physical;
}
__declspec(naked) static void pollHook(){
 __asm {
 pushfd
 push ecx
 push edx
 push eax
 call smokeInput
 add esp,4
 pop edx
 pop ecx
 popfd
 mov ecx,dword ptr ds:[4d48b8h]
 jmp pollContinue
 }
}
static LONG CALLBACK crashTrace(EXCEPTION_POINTERS* e){
 if((e->ExceptionRecord->ExceptionCode & 0xffff0000u)!=0xc0000000u)return EXCEPTION_CONTINUE_SEARCH;
 if(logfile){
  CONTEXT* c=e->ContextRecord;
  fprintf(logfile,"EXCEPTION %08lx at %p EIP=%08lx ESP=%08lx EBP=%08lx EAX=%08lx EBX=%08lx ECX=%08lx EDX=%08lx ESI=%08lx EDI=%08lx first=%p second=%p\n",e->ExceptionRecord->ExceptionCode,e->ExceptionRecord->ExceptionAddress,c->Eip,c->Esp,c->Ebp,c->Eax,c->Ebx,c->Ecx,c->Edx,c->Esi,c->Edi,first,second);
  __try{for(int i=0;i<24;i++)fprintf(logfile," %08lx",((DWORD*)c->Esp)[i]);fprintf(logfile,"\n");}__except(EXCEPTION_EXECUTE_HANDLER){}
  fflush(logfile);
 }
 return EXCEPTION_CONTINUE_SEARCH;
}
static bool installHooks(){
 itemContinue=coop_game::Code(0x425c2d);
 pollContinue=coop_game::Code(0x4631e8);
 seedContinue=coop_game::Code(0x42f568);
 // All hooks have whole-instruction, position-independent prologues. Callback
 // stubs are replaced outright and are never copied into trampolines.
 unsigned char createBytes[]={0x6a,0xff,0x68,0x1b,0x77,0x49,0x00};
 unsigned char destroyBytes[]={0x6a,0xff,0x68,0x99,0x70,0x49,0x00};
 const uint32_t createHandler=(uint32_t)coop_game::Code(0x49771b);
 const uint32_t destroyHandler=(uint32_t)coop_game::Code(0x497099);
 memcpy(createBytes+3,&createHandler,4);memcpy(destroyBytes+3,&destroyHandler,4);
 static const unsigned char activateBytes[]={0x51,0xd9,0xee,0x56,0x8b,0x35,0x14,0x45,0x4b,0x00};
 static const unsigned char tickBytes[]={0x51,0xe8,0x3a,0xf5,0xff,0xff};
 static const unsigned char drawBytes[]={0x8b,0xc1,0x51,0xe8,0x88,0xff,0xff,0xff};
 static const unsigned char alignBytes[]={0x55,0x8b,0xec,0x83,0xe4,0xf8};
 static const unsigned char rectBytes[]={0x83,0xec,0x10,0xd9,0x00};
 static const unsigned char itemBytes[]={0x8b,0x15,0xe8,0x44,0x4b,0x00};
 static const unsigned char pollBytes[]={0x8b,0x0d,0xb8,0x48,0x4d,0x00};
 static const unsigned char seedBytes[]={0xff,0x15,0x98,0x82,0x49,0x00};
 static const unsigned char stageLoadBytes[]={0xf7,0x05,0x78,0xee,0x4c,0x00,0x00,0x80,0x00,0x00};
 static const unsigned char optionsBytes[]={0x83,0xec,0x38,0x8b,0x0d,0x90,0x0c,0x4b,0x00};
 static const unsigned char bombBytes[]={0x8b,0xc1,0xe9,0x29,0x01,0x00,0x00};
 static const unsigned char saveBytes[]={0x81,0xec,0x1c,0x01,0x00,0x00};
 static const unsigned char loopBytes[]={0x8b,0x87,0xb0,0x09,0x00,0x00};
 struct Patch{uintptr_t address;const unsigned char* bytes;size_t count;void* target;void** original;};
 const Patch patches[]={
 {0x4631e2,pollBytes,sizeof(pollBytes),(void*)pollHook,nullptr},
 {0x42f562,seedBytes,sizeof(seedBytes),(void*)seedHook,nullptr},
 {0x4304e0,stageLoadBytes,sizeof(stageLoadBytes),(void*)stageLoadHook,&stageLoadOrig},
 {0x4363f0,createBytes,sizeof(createBytes),(void*)createHook,&createOrig},
 {0x436250,destroyBytes,sizeof(destroyBytes),(void*)destroyHook,&destroyOrig},
 {0x4360e0,activateBytes,sizeof(activateBytes),(void*)activateHook,&activateOrig},
 {0x437640,tickBytes,sizeof(tickBytes),(void*)tickHook,nullptr},
 {0x437650,drawBytes,sizeof(drawBytes),(void*)drawHook,nullptr},
 {0x439eb0,alignBytes,sizeof(alignBytes),(void*)damageHook,&damageOrig},
 {0x4377f0,rectBytes,sizeof(rectBytes),(void*)coop_collision::RectHook,&rectOrig},
 {0x437960,alignBytes,sizeof(alignBytes),(void*)coop_collision::CircleHook,&circleOrig},
 {0x437a60,alignBytes,sizeof(alignBytes),(void*)coop_collision::LaserHook,&laserOrig},
 {0x427360,itemBytes,sizeof(itemBytes),(void*)itemHook,&itemOrig},
 {0x425c27,loopBytes,sizeof(loopBytes),(void*)itemLoopHook,nullptr},
 {0x43bbf0,saveBytes,sizeof(saveBytes),(void*)saveHook,&saveOrig},
 {0x406b90,bombBytes,sizeof(bombBytes),(void*)bombHook,nullptr},
 {0x438590,optionsBytes,sizeof(optionsBytes),(void*)optionsHook,&optionsOrig}
 };
 coop_patch::Registry registry;
 for(const auto& p:patches)registry.Hook100a(p.address,p.bytes,p.count,p.target,p.original);
 coop_resources::Register(registry);coop_targeting::Register(registry);
 coop_collision::Register(registry);coop_items::Register(registry);
 coop_progression::Register(registry,actorEligible);
 coop_homing::Register(registry);
 coop_force::Register(registry);
 coop_effects::Register(registry);
 if(!registry.Validate()){
  ipc->status=0x80000001;
  if(logfile)fprintf(logfile,"Unsupported signature at %08lx; no hooks applied\n",(unsigned long)registry.FailedAddress());
  log("Unsupported executable signature; no game hooks applied");return false;
 }
 if(singleton()){ipc->status=0x80000001;log("Inject at title screen before starting a game");return false;}
 coop_lockstep::ruleFingerprint=cooperativeStateHash;
 if(!registry.Install()){ipc->status=0x80000002;log("Hook installation failed and rolled back");return false;}
 if(logfile)fprintf(logfile,"Verified and installed %zu patch entries\n",registry.Count());
 ipc->status=1;coop_lockstep::Ready();
 log(coop_game::codeShift?"TH12 1.00b cooperative hooks installed":"TH12 1.00a cooperative hooks installed");return true;
}

using ClockFunction=DWORD(WINAPI*)();
static ClockFunction bootstrapClockOriginal;
static volatile LONG bootstrapEntered;
static bool restoreBootstrapClock(){
 auto slot=(void**)0x498298;DWORD protection=0;
 if(!VirtualProtect(slot,sizeof(void*),PAGE_READWRITE,&protection))return false;
 InterlockedExchangePointer(slot,(void*)bootstrapClockOriginal);
 DWORD ignored=0;return VirtualProtect(slot,sizeof(void*),protection,&ignored)!=FALSE;
}
static void bootstrapFailed(uint32_t code){
 ipc->status=0x80000004;coop_lockstep::Fail(code);
 for(;;)Sleep(10); // The owning launcher closes the stopped process.
}
__declspec(noinline) static DWORD WINAPI bootstrapClock(){
 // Steam performs its own normal startup before reaching the game's seed call.
 // This fence observes that call; it does not unpack or replace Steam code.
 if((uintptr_t)_ReturnAddress()!=coop_game::Code(0x42f568))return bootstrapClockOriginal();
 if(GetCurrentThreadId()!=coop_boot::PrimaryThread()||InterlockedCompareExchange(&bootstrapEntered,1,0)!=0)
  bootstrapFailed(15);
 if(!restoreBootstrapClock())bootstrapFailed(16);
 // At this exact point all game code is available, but neither game RNG has
 // been seeded. Preserve that ordering; never install from a polling thread.
 if(!installHooks())bootstrapFailed(17);
 return coop_lockstep::InitialSeed();
}
static bool armSteamBootstrap(){
 if(!coop_boot::PrimaryThread())return false;
 auto slot=(void**)0x498298;DWORD protection=0;
 if(!VirtualProtect(slot,sizeof(void*),PAGE_READWRITE,&protection))return false;
 bootstrapClockOriginal=(ClockFunction)*slot;
 if(!bootstrapClockOriginal){DWORD ignored;VirtualProtect(slot,sizeof(void*),protection,&ignored);return false;}
 InterlockedExchangePointer(slot,(void*)bootstrapClock);
 DWORD ignored=0;if(!VirtualProtect(slot,sizeof(void*),protection,&ignored))return false;
 coop_lockstep::ArmBootstrap();ipc->status=5;
 log("Steam startup fence armed; game hooks are not installed yet");return true;
}
static DWORD WINAPI start(void*){
 char name[64];sprintf_s(name,"Local\\TH12Coop_%lu",GetCurrentProcessId());
 mapping=CreateFileMappingA(INVALID_HANDLE_VALUE,nullptr,PAGE_READWRITE,0,64,name);
 ipc=(Shared*)MapViewOfFile(mapping,FILE_MAP_ALL_ACCESS,0,0,64);
 if(!ipc)return 1;
 ipc->magic=0x50323154;ipc->version=1;
 coop_audit::Initialize();
 logfile=_fsopen("th12coop-native.log","a",_SH_DENYNO);
 AddVectoredExceptionHandler(1,crashTrace);
 if(!coop_game::SelectLayout()){ipc->status=0x80000001;log("Unsupported executable layout; no hooks applied");return 1;}
 if(!coop_lockstep::Initialize(log)){ipc->status=0x80000003;log("Invalid lockstep mapping");return 1;}
 if(!coop_boot::Initialize()){ipc->status=0x80000004;log("Early-start metadata is missing or invalid");return 1;}
 const unsigned char seedCall[]={0xff,0x15,0x98,0x82,0x49,0x00};
 if(coop_game::steam&&memcmp((void*)coop_game::Code(0x42f562),seedCall,sizeof(seedCall))){
  if(armSteamBootstrap())return 0;
  ipc->status=0x80000004;coop_lockstep::Fail(16);log("Cannot arm Steam startup fence safely");return 1;
 }
 return installHooks()?0:1;
}
extern "C" __declspec(dllexport) DWORD WINAPI CoopStart(void* arg){return start(arg);}
BOOL WINAPI DllMain(HINSTANCE module,DWORD reason,LPVOID){
 if(reason==DLL_PROCESS_ATTACH){DisableThreadLibraryCalls(module);HANDLE h=CreateThread(nullptr,0,start,nullptr,0,nullptr);if(h)CloseHandle(h);}
 return TRUE;
}


