#pragma once
#ifdef COOP_SMOKE_TEST
#include "coop_audit_ecl.h"
#include "coop_audit_bullets.h"
namespace coop_sync_audit {
static DWORD WINAPI TryAnmLock(void* parameter){
 auto acquired=(volatile LONG*)parameter;
 if(TryEnterCriticalSection((LPCRITICAL_SECTION)0x4cf1d0)){
  InterlockedExchange(acquired,1);LeaveCriticalSection((LPCRITICAL_SECTION)0x4cf1d0);
 }
 return 0;
}
static bool OtherThreadCanEnter(){
 volatile LONG acquired=0;
 HANDLE worker=CreateThread(nullptr,0,TryAnmLock,(void*)&acquired,0,nullptr);
 if(!worker)return true;
 WaitForSingleObject(worker,INFINITE);CloseHandle(worker);return acquired!=0;
}
}
static uint32_t RunSyncAudit(uint32_t caseId,uint32_t* details){
 using namespace coop_resources;
 memset(details,0,32*sizeof(uint32_t));details[0]=caseId;
 uint32_t failure=0;auto check=[&](bool condition,unsigned bit){if(!condition)failure|=1u<<bit;};
 if(caseId==5||caseId==6)return coop_bullet_audit::Run(caseId,details);
 if(caseId==3||caseId==4)return coop_ecl_audit::Run(caseId,details);
 if(caseId==1){
  if(!enabled||At<int>(0x4b0c90)!=0||At<int>(0x4b0c94)!=0)return 0x80000000u;
  At<int>(0x4b0ca0)=2;Player()=secondActor;
  check(((int(__cdecl*)())coop_game::Code(0x406bd0))()==0,0);
  uintptr_t emitter=0,beam=0;
  coop_effects::Visit([&](uintptr_t vm,uintptr_t p,bool e){
   if(e&&At<uint32_t>(p+0x30)==2)emitter=vm;
  });
  if(!emitter)return 0x40000000u;
  ((int(__fastcall*)(unsigned char*,void*))coop_game::Code(0x46a2e0))((unsigned char*)emitter,nullptr);
  coop_effects::Visit([&](uintptr_t,uintptr_t p,bool e){if(!e)beam=p;});
  Player()=firstActor;if(!beam)return 0x20000000u;
  auto baseline=coop_effects::StateHash();
  const uint32_t offsets[]={0x2a0,0x2b8,0x328,0x330,0x348,0x360};
  for(unsigned n=0;n<sizeof(offsets)/sizeof(offsets[0]);++n){
   auto& field=At<uint32_t>(beam+offsets[n]);auto original=field;field^=0x100;
   bool detected=baseline!=coop_effects::StateHash();details[1+n]=detected;
   check(detected,n+1);field=original;check(baseline==coop_effects::StateHash(),8);
  }
 }else if(caseId==2){
  details[1]=At<uint32_t>(0x4cee78)&0x8000;
  if(!details[1])return 0x10000000u; // This fixture requires native multithreading.
  auto before=At<unsigned char>(0x4cf221);
  {
   coop_effects::AnmListGuard outer;
   check(!coop_sync_audit::OtherThreadCanEnter(),0);
   {coop_effects::AnmListGuard inner;check(!coop_sync_audit::OtherThreadCanEnter(),1);}
   check(At<unsigned char>(0x4cf221)==(unsigned char)(before+1),2);
   check(!coop_sync_audit::OtherThreadCanEnter(),3);
  }
  check(At<unsigned char>(0x4cf221)==before,4);
  check(coop_sync_audit::OtherThreadCanEnter(),5);
 }else if(caseId==7){
  // The raw native device word is broader than the network input protocol.
  // Execute the real barrier with a precommitted legal frame; do not tick the
  // world or inject a physical key. The temporary IPC is fixture-only.
  if(coop_lockstep::Active())return 0x80000007u;
  auto oldState=coop_lockstep::state;auto oldFrame=coop_lockstep::nextFrame;
  static coop_lockstep::Shared temporary;
  memset(&temporary,0,sizeof(temporary));temporary.h.flags=1;
  temporary.h.heartbeat=GetTickCount();temporary.input[1].sequence=1;
  coop_lockstep::state=&temporary;coop_lockstep::nextFrame=1;
  uint32_t result=coop_lockstep::Poll(UINT32_MAX,first,second);
  details[1]=temporary.h.localMask;details[2]=temporary.h.consumed;
  check(result==0&&temporary.h.consumed==1,0);
  check(temporary.h.localMask==0x1fb,1);
  coop_lockstep::state=oldState;coop_lockstep::nextFrame=oldFrame;
 }else return 0x20000000u;
 details[31]=failure;return failure;
}
#endif
