#pragma once
#ifdef COOP_SMOKE_TEST
#include "coop_ecl_digest.h"
namespace coop_ecl_audit {
// All records are zeroed, local fixture allocations. No raw address is reported.
struct Synthetic {
 unsigned char full[0x3000],async[0x1024],manager[0x90],enemyManager[0x100];
 uintptr_t rows[4],asyncNode[3],enemyNode[3];
 unsigned char code[0x300];
 void Init(){
  memset(this,0,sizeof(*this));auto f=(uintptr_t)full,c=(uintptr_t)async,m=(uintptr_t)manager;
  coop_resources::At<uintptr_t>(f+0x102c)=m;
  coop_resources::At<uintptr_t>(f+4)=f+8;
  coop_resources::At<uintptr_t>(f+0x1030)=f+8;
  coop_resources::At<uintptr_t>(f+0x1034)=(uintptr_t)asyncNode;
  asyncNode[0]=c;asyncNode[2]=f+0x1030;
  // Deliberately reverse address order relative to name-sorted row identity.
  rows[1]=(uintptr_t)code+0x180;rows[3]=(uintptr_t)code+0x20;
  coop_resources::At<uint32_t>(m+4)=1;coop_resources::At<uint32_t>(m+8)=2;
  coop_resources::At<uintptr_t>(m+0xc)=(uintptr_t)code;
  coop_resources::At<uintptr_t>(m+0x8c)=(uintptr_t)rows;
  for(auto context:{f+8,c}){
   coop_resources::At<uintptr_t>(context+4)=rows[context==c?3:1]+0x20;
   coop_resources::At<uintptr_t>(context+0x1014)=f;
   coop_resources::At<uint32_t>(context+0x1008)=20;
   coop_resources::At<uint32_t>(context+0x100c)=16;
   coop_resources::At<uint8_t>(context+0x101c)=2;
  }
  coop_resources::At<uint32_t>(c+0x1010)=7;
  enemyNode[0]=f;coop_resources::At<uintptr_t>((uintptr_t)enemyManager+0x68)=(uintptr_t)enemyNode;
 }
};
static uint64_t Current(Synthetic& fixture){
 auto saved=coop_lockstep::state;
 static coop_lockstep::Shared temporary;
 memset(&temporary,0,sizeof(temporary));coop_lockstep::state=&temporary;
 auto old=coop_resources::At<uintptr_t>(0x4b43dc);
 coop_resources::At<uintptr_t>(0x4b43dc)=(uintptr_t)fixture.enemyManager;
 coop_lockstep::Digest digest={};
 uint64_t result=coop_lockstep::Fingerprint(first,second,digest);
 coop_resources::At<uintptr_t>(0x4b43dc)=old;coop_lockstep::state=saved;
 return result;
}
static bool Extended(Synthetic& fixture,uint64_t& result){
 coop_ecl_digest::BeginFrame();coop_lockstep::Hash h;
 bool valid=coop_ecl_digest::Append(h,(uintptr_t)fixture.full);result=h.value;return valid;
}
static uint32_t Run(uint32_t caseId,uint32_t* details){
 using namespace coop_resources;uint32_t failure=0;
 auto check=[&](bool value,unsigned bit){details[1+bit]=value;if(!value)failure|=1u<<bit;};
 auto a=(Synthetic*)VirtualAlloc(nullptr,sizeof(Synthetic),MEM_RESERVE|MEM_COMMIT,PAGE_READWRITE);
 auto b=(Synthetic*)VirtualAlloc(nullptr,sizeof(Synthetic),MEM_RESERVE|MEM_COMMIT,PAGE_READWRITE);
 if(!a||!b){if(a)VirtualFree(a,0,MEM_RELEASE);if(b)VirtualFree(b,0,MEM_RELEASE);return 0x80000000u;}
 a->Init();b->Init();uint64_t baseline=Current(*a),extended=0,value=0;
 if(caseId==3){
  check(Extended(*a,extended),0);
  auto& primaryPc=At<uintptr_t>((uintptr_t)a->full+12);auto savedPc=primaryPc;primaryPc+=16;
  check(Current(*a)!=baseline,1);check(Extended(*a,value)&&value!=extended,2);primaryPc=savedPc;
  auto& asyncTime=At<uint32_t>((uintptr_t)a->async);asyncTime=0x3f800000;
  check(Current(*a)!=baseline,3);check(Extended(*a,value)&&value!=extended,4);asyncTime=0;
  auto& asyncPc=At<uintptr_t>((uintptr_t)a->async+4);savedPc=asyncPc;asyncPc+=16;
  check(Current(*a)!=baseline,5);check(Extended(*a,value)&&value!=extended,6);asyncPc=savedPc;
  check(Current(*b)==baseline&&Extended(*b,value)&&value==extended,7);
  At<uint32_t>((uintptr_t)b->async+0x1018)=0xdeadbeef;
  At<uint32_t>((uintptr_t)b->async+0x1020)=0xfeedcafe;
  At<uint32_t>((uintptr_t)b->async+0x101c)|=0xabcdef00;
  At<uint32_t>((uintptr_t)b->async+8)=0x76543210;
  check(Extended(*b,value)&&value==extended,8);
 }else{
  check(Extended(*a,extended),0);
  auto& subCount=At<uint32_t>((uintptr_t)a->manager+8);subCount=coop_ecl_digest::MaxSubs+1;
  check(!Extended(*a,value),1);subCount=2;
  auto& pc=At<uintptr_t>((uintptr_t)a->full+12);auto saved=pc;pc=a->rows[1]+0x10+coop_ecl_digest::MaxPcDelta;
  check(!Extended(*a,value),2);pc=saved;
  auto& sp=At<uint32_t>((uintptr_t)a->async+0x1008);sp=0x1004;
  check(!Extended(*a,value),3);sp=20;
  a->asyncNode[1]=(uintptr_t)a->asyncNode;
  check(!Extended(*a,value),4);a->asyncNode[1]=0;
  // Sample actual native contexts without modifying them. Report counts and
  // normalized deltas only; absence of async contexts is not an async pass.
  coop_ecl_digest::BeginFrame();coop_lockstep::Hash live;
  auto manager=At<uintptr_t>(0x4b43dc),node=manager?At<uintptr_t>(manager+0x68):0;
  uint32_t enemies=0,visited=0;bool valid=true;
  while(node&&visited<4096){++visited;auto full=At<uintptr_t>(node);if(full){++enemies;if(!coop_ecl_digest::Append(live,full)){valid=false;break;}}node=At<uintptr_t>(node+4);}
  check(valid&&!node&&enemies>0,5);
  const auto stats=coop_ecl_digest::stats;
  details[16]=enemies;details[17]=stats.managers;details[18]=stats.subs;
  details[19]=stats.contexts;details[20]=stats.asyncContexts;details[21]=stats.maxDelta;details[22]=stats.error;
 }
 VirtualFree(b,0,MEM_RELEASE);VirtualFree(a,0,MEM_RELEASE);
 details[31]=failure;return failure;
}
}
#endif
