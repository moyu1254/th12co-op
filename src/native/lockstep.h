#pragma once
// Versioned native/launcher IPC. Only input and deterministic state digests cross
// this interface; the game renders and plays audio independently on each PC.
#include <windows.h>
#include <stdint.h>
#include <string.h>
#include <stdio.h>
#include "game_layout.h"

namespace coop_lockstep {
constexpr uint32_t Magic=0x4e323154,Version=1,Capacity=256,Bytes=12544;
struct Input {volatile LONG sequence;uint32_t p1,p2,flags;};
struct Digest {volatile LONG sequence;uint32_t lo,hi,scene,stage,players,gameFrame,rng;};
struct Header {
 uint32_t magic,version,bytes,capacity,flags,seed;
 volatile uint32_t heartbeat,abortCode;
 volatile uint32_t status,requested,consumed,completed,hashLo,hashHi,error,waitSince,p1,p2,threadId,rulesVersion,localMask,localFrame,deviceReady;
 uint32_t primaryThreadId;
 volatile uint32_t components[6];
 uint32_t reserved120[34];
};
struct Shared {Header h;Input input[Capacity];Digest digest[Capacity];};
static_assert(sizeof(Header)==256,"lockstep header ABI");
static_assert(sizeof(Shared)==Bytes,"lockstep mapping ABI");
static Shared* state;
static HANDLE mapping;
static HANDLE requestEvent,commitEvent;
static HANDLE pendingStage;
static uint32_t nextFrame=1;
static void (*logger)(const char*);
static uint64_t (*ruleFingerprint)();
template<class T> static T& at(uintptr_t p){return *reinterpret_cast<T*>(p);}
static bool Active(){return state&&(state->h.flags&1)!=0;}
static uint32_t PrimaryThread(){return state?state->h.primaryThreadId:0;}
static void ArmBootstrap(){if(Active())state->h.status=5;}
static uint32_t InputFor(int player){return player?state->h.p2:state->h.p1;}
static DWORD InitialSeed(){
 if(Active())return state->h.seed;
 return ((DWORD(WINAPI*)())at<uintptr_t>(0x498298))();
}
static void Fail(uint32_t code){
 if(!state)return;
 state->h.error=code;MemoryBarrier();state->h.status=4;
 if(logger){char line[96];sprintf_s(line,"Lockstep stopped at frame %u, error %u",nextFrame,code);logger(line);}
}
static void WaitThread(HANDLE thread){
 if(!Active()||!thread)return;
 const DWORD began=GetTickCount();
 for(;;){
  DWORD result=WaitForSingleObject(thread,1);
  if(result==WAIT_OBJECT_0)return;
  if(result==WAIT_FAILED){Fail(8);break;}
  // ANM workers wait for the game thread to upload requested textures. Service
  // the original upload dispatcher while joining; this does not tick gameplay.
  if(at<uintptr_t>(0x4ce8cc)&&((int(__cdecl*)())coop_game::Code(0x4603b0))()!=0){Fail(10);break;}
  // The stage worker also awaits the native queued audio command at 0x4221F8.
  // Supervisor normally drains it before input polling at 0x42F008.
  ((void(__cdecl*)())coop_game::Code(0x453f80))();
  if(state->h.abortCode||(uint32_t)(GetTickCount()-state->h.heartbeat)>5000){Fail(1);break;}
  if((uint32_t)(GetTickCount()-began)>30000){Fail(9);break;}
 }
 for(;;)Sleep(10);
}
static void StageLoadStarted(){if(Active())pendingStage=at<HANDLE>(0x4cf0dc);}
static void FinishStageLoad(){
 // Creation occurs inside Supervisor's lock and before its final draw. The
 // worker waits for both that lock and the ANM draw sentinel. Join at the next
 // raw-input boundary, after the preceding frame has rendered.
 if(Active()&&pendingStage){WaitThread(pendingStage);pendingStage=nullptr;}
}
static void FinishInitialLoad(){
 if(!Active())return;
 // Initial scorefile construction consumes replay RNG on this worker. Join
 // before publishing state; its completion must not depend on disk speed.
 auto loading=at<uintptr_t>(0x4b44f8);
 if(loading)WaitThread(at<HANDLE>(loading+0x14));
}
static void FinishMenuLoad(){
 if(!Active())return;
 auto menu=at<uintptr_t>(0x4b4530),loading=at<uintptr_t>(0x4b44f8);
 // MainMenu's worker intentionally waits for 180 LoadingThread draws before
 // enabling menu input. Once that native condition is met, join its completion
 // before the next simulation frame instead of racing its Sleep(16) wake-up.
 if(menu&&(!loading||at<int>(loading+0x4f4)>=180))WaitThread(at<HANDLE>(menu+0x5c20));
}
struct Hash {
 uint64_t value=14695981039346656037ull;
 void word(uint32_t x){for(int i=0;i<4;i++){value^=(uint8_t)x;value*=1099511628211ull;x>>=8;}}
 void words(uintptr_t p,size_t bytes){for(size_t n=0;n<bytes;n+=4)word(at<uint32_t>(p+n));}
};
static void PlayerHash(Hash& h,unsigned char* p){
 h.word(p?1:0);if(!p)return;auto a=(uintptr_t)p;
 h.words(a+0x97c,0x14);h.word(at<uint32_t>(a+0xa28));
 h.words(a+0xa30,12);h.words(a+0xa44,12);h.words(a+0xc400,12);
 h.word(at<uint32_t>(a+0xc414));h.word(at<uint32_t>(a+0xc418));h.word(at<uint32_t>(a+0xc598));
 for(uint32_t n=0;n<256;n++){
  auto b=a+0xa58+n*0x78;auto alive=at<uint32_t>(b+0x48);h.word(alive);
  if(alive){h.word(n);h.words(b,12);h.words(b+0x14,0x30);h.words(b+0x60,0x10);}
 }
 // Activated native damage sources are zero-initialized in full. Exclude the
 // timer speed pointer at +0x58; geometry, timers and damage remain scalar.
 for(uint32_t n=0;n<128;n++){
  auto source=a+0x8988+n*0x74;auto flags=at<uint32_t>(source+0x70);h.word(flags);
  if(flags&1){h.word(n);h.words(source,0x58);h.words(source+0x5c,0x14);}
 }
}
static uint64_t Fingerprint(unsigned char* p1,unsigned char* p2,Digest& d){
 Hash total,parts[6];
 if(ruleFingerprint){const uint64_t rules=ruleFingerprint();parts[0].word((uint32_t)rules);parts[0].word((uint32_t)(rules>>32));}
 const uintptr_t globals[]={0x4b0c44,0x4b0c48,0x4b0c4c,0x4b0c50,0x4b0c54,0x4b0c58,0x4b0c78,0x4b0c90,0x4b0c94,0x4b0c98,0x4b0c9c,0x4b0ca0,0x4b0ca4,0x4b0ca8,0x4b0cb0,0x4b0cbc,0x4b0cc4,0x4b0cdc,0x4b2ed0};
 for(auto a:globals)parts[0].word(at<uint32_t>(a));
 parts[0].word(at<uint32_t>(0x4b0ccc));
 // Constructors zero these bounded common fields. Never hash ANM/payload or
 // timer-speed pointers, which vary between processes and distributions.
 auto bomb=at<uintptr_t>(0x4b43c4);parts[0].word(bomb?1u:0u);
 if(bomb){parts[0].words(bomb+0x14,12);parts[0].words(bomb+0x28,12);parts[0].word(at<uint32_t>(bomb+0x3c));parts[0].words(bomb+0x508,0x18);}
 auto spell=at<uintptr_t>(0x4b43cc);parts[0].word(spell?1u:0u);
 if(spell){parts[0].words(spell+0x24,12);parts[0].words(spell+0x78,12);}
 parts[0].word(at<uint16_t>(0x4ce568));parts[0].word(at<uint32_t>(0x4ce56c));
 auto game=at<uintptr_t>(0x4b44e8);parts[0].word(game?at<uint32_t>(game+0x60):0);
 PlayerHash(parts[1],p1);PlayerHash(parts[2],p2);
 auto enemy=at<uintptr_t>(0x4b43dc);
 parts[3].word(enemy?1:0);
 if(enemy){
  parts[3].words(enemy+0x10,12);parts[3].words(enemy+0x50,12);parts[3].words(enemy+0x70,12);
  auto node=at<uintptr_t>(enemy+0x68);uint32_t count=0;
  while(node&&count<4096){
   ++count;
   auto full=at<uintptr_t>(node);
   // Retail 0x41318E adds 0x1040 to the list entry before EnemyData update.
   if(full){
    auto e=full+0x1040;parts[3].words(e+0x34,12);parts[3].words(e+0x68,12);parts[3].words(e+0x23c,0x30);parts[3].words(e+0x1608,0x18);parts[3].word(at<uint32_t>(e+0x16b8));
    // ECL stacks contain raw instruction pointers; only verified scalar VM
    // fields are included until instruction offsets can be normalized safely.
    auto context=full+8;parts[3].word(at<uint32_t>(context));parts[3].words(context+0x1008,12);
    parts[3].word(at<uint32_t>(context+0x1018));parts[3].word(at<uint8_t>(context+0x101c));
    // Reset 0x411E22 and ins18/19 manipulate only bit zero; upper bits are
    // uninitialized allocation contents, not ECL state.
    parts[3].word(at<uint32_t>(context+0x1020)&1u);
   }
   node=at<uintptr_t>(node+4);
  }
  if(node){Fail(11);return 0;}
  parts[3].word(count);
 }
 auto bullets=at<uintptr_t>(0x4b43c8);parts[4].word(bullets?1:0);
 if(bullets)for(uint32_t n=0;n<2000;n++){
  auto b=bullets+0x64+n*0x9f8;auto alive=at<uint16_t>(b+0x532);parts[4].word(alive);
  if(alive){parts[4].word(n);parts[4].words(b+0x4bc,0x28);parts[4].word(at<uint32_t>(b+0x528));parts[4].word(at<uint32_t>(b+0x548));parts[4].words(b+0x550,0x1b0);parts[4].word(at<uint32_t>(b+0x9f4));}
 }
 auto items=at<uintptr_t>(0x4b44f0);parts[4].word(items?1:0);
 if(items)for(uint32_t n=0;n<2664;n++){
  auto item=items+0x14+n*0x9d8;auto alive=at<uint32_t>(item+0x9b0);parts[4].word(alive);
  if(alive){parts[4].word(n);parts[4].word(at<uint32_t>(item+0x9b4));parts[4].words(item+0x96c,24);parts[4].words(item+0x988,12);parts[4].words(item+0x9bc,8);}
 }
 auto lasers=at<uintptr_t>(0x4b44f4);parts[4].word(lasers?1:0);
 if(lasers){
  auto laser=at<uintptr_t>(lasers+0x18);uint32_t count=0;
  auto allocated=at<uint32_t>(lasers+0x468);parts[4].word(allocated);
  if(allocated>256){Fail(12);return 0;}
  while(laser&&count<256){
   ++count;auto kind=at<uint32_t>(laser);
   uint32_t type=kind==0x4a0654?0:kind==0x4a0704?1:kind==0x4a06ac?2:3;
   if(type==3){Fail(14);return 0;}
   parts[4].word(type);parts[4].word(at<uint32_t>(laser+0xc));
   parts[4].words(laser+0x14,12);parts[4].words(laser+0x28,12);parts[4].words(laser+0x3c,12);
   parts[4].words(laser+0x50,0x2c);parts[4].word(at<uint8_t>(laser+0x7c));
   parts[4].word(at<uint32_t>(laser+0x430));parts[4].word(at<uint32_t>(laser+0x44c));
   if(kind==0x4a06ac){
    auto nodes=at<uint32_t>(laser+0x470);auto data=at<uintptr_t>(laser+0xf9c);
    if(nodes>256||(nodes&&!data)){Fail(13);return 0;}
    parts[4].word(nodes);if(nodes)parts[4].words(data,nodes*20);
   }
   laser=at<uintptr_t>(laser+8);
  }
  if(laser){Fail(12);return 0;}
  parts[4].word(count);
 }
 auto menu=at<uintptr_t>(0x4b4530),pause=at<uintptr_t>(0x4b4510);
 d.scene=at<uint32_t>(0x4cee3c);d.stage=at<uint32_t>(0x4b0cb0);d.gameFrame=at<uint32_t>(0x4b0cbc);d.rng=at<uint16_t>(0x4ce568);
 d.players=(p1?at<uint32_t>((uintptr_t)p1+0xa28):255)|((p2?at<uint32_t>((uintptr_t)p2+0xa28):255)<<8);
 parts[5].word(d.scene);parts[5].word(at<uint32_t>(0x4cee40));parts[5].word(menu?at<uint32_t>(menu+0x1c):0xffffffff);
 if(pause){parts[5].word(at<uint32_t>(pause));parts[5].word(at<uint32_t>(pause+4));parts[5].word(at<uint32_t>(pause+0x38));parts[5].word(at<uint32_t>(pause+0x3c));}
 for(int n=0;n<6;n++){state->h.components[n]=(uint32_t)parts[n].value;total.word((uint32_t)parts[n].value);total.word((uint32_t)(parts[n].value>>32));}
 return total.value;
}
static uint32_t Poll(uint32_t physical,unsigned char*& p1,unsigned char*& p2){
 if(!Active())return physical;
 FinishInitialLoad();
 FinishStageLoad();
 FinishMenuLoad();
 auto& h=state->h;
 h.threadId=GetCurrentThreadId();h.deviceReady=at<uintptr_t>(0x4ce8f0)?1:0;
 const uint32_t completed=nextFrame-1;
 auto& d=state->digest[completed%Capacity];
 uint64_t hash=Fingerprint(p1,p2,d);d.lo=(uint32_t)hash;d.hi=(uint32_t)(hash>>32);
 if(h.status==4)for(;;)Sleep(10);
 MemoryBarrier();InterlockedExchange(&d.sequence,(LONG)completed);
 h.hashLo=d.lo;h.hashHi=d.hi;h.completed=completed;
 h.localMask=physical;h.localFrame=nextFrame;
 h.waitSince=GetTickCount();MemoryBarrier();h.requested=nextFrame;h.status=2;
 if(requestEvent)SetEvent(requestEvent);
 auto& in=state->input[nextFrame%Capacity];
 for(;;){
  if(h.status==4){Sleep(10);continue;}
  if(h.abortCode){Fail(h.abortCode);continue;}
  if((uint32_t)(GetTickCount()-h.heartbeat)>5000){Fail(1);continue;}
  if((uint32_t)(GetTickCount()-h.waitSince)>(nextFrame==1?125000u:40000u)){Fail(2);continue;}
  const uint32_t sequence=(uint32_t)InterlockedCompareExchange(&in.sequence,0,0);
  if(sequence==nextFrame){
   uint32_t a=in.p1,b=in.p2,flags=in.flags;MemoryBarrier();
   if((uint32_t)in.sequence!=sequence){Fail(3);continue;}
   if((a&~0x1fbu)||(b&~0xfbu)||flags){Fail(4);continue;}
   h.p1=a;h.p2=b;h.consumed=nextFrame++;h.status=3;return a;
  }
  if(sequence>nextFrame){Fail(5);continue;}
  if(commitEvent)WaitForSingleObject(commitEvent,10);else Sleep(1);
 }
}
using CreateMutexFn=HANDLE(WINAPI*)(LPSECURITY_ATTRIBUTES,BOOL,LPCSTR);
static CreateMutexFn originalMutex;
static HANDLE WINAPI MutexHook(LPSECURITY_ATTRIBUTES attrs,BOOL owner,LPCSTR name){
 if(Active()&&(state->h.flags&2)&&name&&!strcmp(name,"Touhou 12 App")){
  char unique[64];sprintf_s(unique,"TH12CoopTest_%lu",GetCurrentProcessId());return originalMutex(attrs,owner,unique);
 }
 return originalMutex(attrs,owner,name);
}
static bool Initialize(void(*writeLog)(const char*)){
 logger=writeLog;char name[64];sprintf_s(name,"Local\\TH12CoopNet_%lu",GetCurrentProcessId());
 mapping=OpenFileMappingA(FILE_MAP_ALL_ACCESS,FALSE,name);
 if(!mapping)return true; // Offline mode has no net mapping.
 state=(Shared*)MapViewOfFile(mapping,FILE_MAP_ALL_ACCESS,0,0,Bytes);
 if(!state)return false;
 auto& h=state->h;
 if(h.magic!=Magic||h.version!=Version||h.bytes!=Bytes||h.capacity!=Capacity||h.rulesVersion!=2||!(h.flags&1)||(h.flags&~3u)){Fail(6);return false;}
 h.rulesVersion=2;
 sprintf_s(name,"Local\\TH12CoopNetRequest_%lu",GetCurrentProcessId());requestEvent=OpenEventA(EVENT_MODIFY_STATE,FALSE,name);
 sprintf_s(name,"Local\\TH12CoopNetCommit_%lu",GetCurrentProcessId());commitEvent=OpenEventA(SYNCHRONIZE,FALSE,name);
 if(h.flags&2){
  DWORD old;auto slot=(void**)0x4980e8;
  if(!VirtualProtect(slot,4,PAGE_READWRITE,&old)){Fail(7);return false;}
  originalMutex=(CreateMutexFn)*slot;*slot=(void*)MutexHook;
  DWORD ignored;VirtualProtect(slot,4,old,&ignored);
 }
 return true;
}
static DWORD WINAPI DeviceReadyWorker(void*){
 char name[64];sprintf_s(name,"Local\\TH12CoopReady_%lu",GetCurrentProcessId());
 HANDLE handle=CreateFileMappingA(INVALID_HANDLE_VALUE,nullptr,PAGE_READWRITE,0,16,name);
 auto ready=(volatile uint32_t*)MapViewOfFile(handle,FILE_MAP_ALL_ACCESS,0,0,16);
 if(!ready){if(handle)CloseHandle(handle);return 1;}
 ready[0]=0x52323154;ready[1]=1;
 while(!at<uintptr_t>(0x4ce8f0))Sleep(10);
 MemoryBarrier();ready[2]=1;
 if(Active())state->h.deviceReady=1;
 return 0; // Mapping stays alive for this process, like the input ABI.
}
static void Ready(){
 HANDLE worker=CreateThread(nullptr,0,DeviceReadyWorker,nullptr,0,nullptr);if(worker)CloseHandle(worker);
 if(Active()){state->h.status=1;if(logger)logger("Native lockstep input barrier ready");}
}
}
