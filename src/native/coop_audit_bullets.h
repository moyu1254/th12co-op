#pragma once
#ifdef COOP_SMOKE_TEST
namespace coop_bullet_audit {
// Private, zeroed fixtures: no original bullet/laser allocation is modified.
// Offsets are proved by the native pool memset and Ex setup/update routines.
struct TimerField { uint32_t flag,offset; };
static const TimerField timers[]={
 {1,0x700},{4,0x734},{8,0x768},{0x70,0x79c},{0x1000,0x804},
 {0x20000,0x838},{0x40000,0x86c},{0x800000,0x8a0},
 {0x2000000,0x8d4},{0x8000000,0x908},{0x20000000,0x93c},{0x400,0x970}
};
static const TimerField operands[]={
 {4,0x748},{4,0x750},{4,0x754},{4,0x758},{4,0x75c},
 {8,0x77c},{8,0x780},{8,0x790},
 {0x70,0x7b0},{0x70,0x7b4},{0x70,0x7c4},{0x70,0x7c8},{0x70,0x7cc},
 {0x100,0x7e4},{0x100,0x7f8},{0x100,0x7fc},{0x100,0x800},
 {0x800000,0x8b4},{0x800000,0x8b8},{0x800000,0x8c8},
 {0x2000000,0x8e8},{0x2000000,0x8f0},{0x2000000,0x8f4},{0x2000000,0x8f8},{0x2000000,0x8fc},
 {0x8000000,0x924},{0x8000000,0x928},{0x8000000,0x92c},{0x8000000,0x930},
 {0x20000000,0x950},{0x20000000,0x958},{0x20000000,0x95c},{0x20000000,0x960},{0x20000000,0x964},
 {0x400,0x998}
};
struct Synthetic {
 unsigned char pool[0x4debe0],laserManager[0x470],laser[0x1000];
 float scale;
 uintptr_t Bullet(){return (uintptr_t)pool+0x64;}
 void Init(){
  using namespace coop_resources;
  memset(this,0,sizeof(*this));scale=1.f;auto b=Bullet();
  At<uint16_t>(b+0x532)=1;At<uint32_t>(b+0x528)=4;At<uint32_t>(b+0x548)=18;
  At<float>(b+0x4c8)=1.f;At<float>(b+0x4d4)=1.f;
  At<float>(b+0x748)=.25f;At<float>(b+0x750)=.25f;At<uint32_t>(b+0x75c)=1;
  At<int32_t>(b+0x734)=-1;
  for(auto t:timers){At<uintptr_t>(b+t.offset+12)=(uintptr_t)&scale;At<uint32_t>(b+t.offset+16)=1;}
  At<uintptr_t>(b+0x9e4)=(uintptr_t)&scale;At<uint32_t>(b+0x9e8)=1;
  At<uintptr_t>((uintptr_t)laserManager+0x18)=(uintptr_t)laser;
  At<uint32_t>((uintptr_t)laserManager+0x468)=1;
  At<uint32_t>((uintptr_t)laser)=0x4a0654; // Native line-laser vtable identity.
  At<uint32_t>((uintptr_t)laser+0xc)=2;
 }
};
struct Sample {uint64_t value;bool valid;};
static Sample Current(Synthetic& fixture){
 using namespace coop_resources;
 auto saved=coop_lockstep::state;
 static coop_lockstep::Shared temporary;
 memset(&temporary,0,sizeof(temporary));coop_lockstep::state=&temporary;
 auto bullets=At<uintptr_t>(0x4b43c8),lasers=At<uintptr_t>(0x4b44f4);
 At<uintptr_t>(0x4b43c8)=(uintptr_t)fixture.pool;
 At<uintptr_t>(0x4b44f4)=(uintptr_t)fixture.laserManager;
 coop_lockstep::Digest digest={};
 Sample result={coop_lockstep::Fingerprint(first,second,digest),temporary.h.status!=4};
 At<uintptr_t>(0x4b43c8)=bullets;At<uintptr_t>(0x4b44f4)=lasers;coop_lockstep::state=saved;
 return result;
}
// Native ExAccel: EAX=bullet, EAX=result, no stack arguments. Keep the custom
// ABI at a non-inlined boundary so the caller assumes ordinary volatile state.
__declspec(noinline) static int Accel(uintptr_t bullet){
 uintptr_t function=coop_game::Code(0x40b6d0);int result;
 __asm {
  mov eax,bullet
  call dword ptr[function]
  mov result,eax
 }
 return result;
}
static uint32_t Run(uint32_t caseId,uint32_t* details){
 using namespace coop_resources;uint32_t failure=0;
 auto check=[&](bool value,unsigned bit){details[1+bit]=value;if(!value)failure|=1u<<bit;};
 auto a=(Synthetic*)VirtualAlloc(nullptr,sizeof(Synthetic),MEM_RESERVE|MEM_COMMIT,PAGE_READWRITE);
 auto b=(Synthetic*)VirtualAlloc(nullptr,sizeof(Synthetic),MEM_RESERVE|MEM_COMMIT,PAGE_READWRITE);
 if(!a||!b){if(a)VirtualFree(a,0,MEM_RELEASE);if(b)VirtualFree(b,0,MEM_RELEASE);return 0x80000000u;}
 a->Init();b->Init();auto x=a->Bullet(),y=b->Bullet();
 if(caseId==5){
  // Equal visible kinematics/Ex definitions, different next native branch.
  At<int32_t>(y+0x734)=0;At<int32_t>(y+0x738)=1;At<float>(y+0x73c)=1.f;
  auto beforeA=Current(*a),beforeB=Current(*b);
  check(beforeA.valid&&beforeB.valid&&beforeA.value!=beforeB.value,0);
  float oldScale=At<float>(0x4b2ed0);At<float>(0x4b2ed0)=1.f;
  int resultA=Accel(x),resultB=Accel(y);
  At<float>(0x4b2ed0)=oldScale;
  check(resultA==0&&resultB==1,1);
  check(At<float>(x+0x4d4)==1.25f&&At<float>(x+0x4c8)==1.25f&&At<float>(y+0x4d4)==1.f&&At<float>(y+0x4c8)==1.f,2);
  check(At<uint32_t>(x+0x528)==4&&At<uint32_t>(y+0x528)==0,3);
  check(At<int32_t>(x+0x738)==1&&At<int32_t>(y+0x738)==1,4);
  auto afterA=Current(*a),afterB=Current(*b);
  check(afterA.valid&&afterB.valid&&afterA.value!=afterB.value,5);
  details[16]=(uint32_t)resultA;details[17]=(uint32_t)resultB;
  details[18]=At<uint32_t>(x+0x4d4);details[19]=At<uint32_t>(y+0x4d4);
  details[20]=At<uint32_t>(x+0x738);details[21]=At<uint32_t>(y+0x738);
 }else if(caseId==6){
  bool timerOk=true,operandOk=true,interpOk=true,restoreOk=true,pointerOk=true;
  uint32_t timerChecks=0,operandChecks=0,interpChecks=0,pointerChecks=0,misses=0,firstMiss=0;
  auto mutate=[&](uintptr_t address,uint32_t offset){
   auto before=Current(*a);auto& value=At<uint32_t>(address);auto old=value;value^=1;
   auto changed=Current(*a);value=old;auto restored=Current(*a);
   bool detected=before.valid&&changed.valid&&before.value!=changed.value;
   restoreOk&=restored.valid&&restored.value==before.value;
   if(!detected){++misses;if(!firstMiss)firstMiss=offset;}
   return detected;
  };
  for(auto t:timers){
   At<uint32_t>(x+0x528)=t.flag;
   for(uint32_t j=0;j<12;j+=4){++timerChecks;timerOk&=mutate(x+t.offset+j,t.offset+j);}
  }
  for(auto field:operands){At<uint32_t>(x+0x528)=field.flag;++operandChecks;operandOk&=mutate(x+field.offset,field.offset);}
  At<uint32_t>(x+0x528)=0x2000000;
  for(uint32_t offset=0x9a8;offset<0x9e4;offset+=4){++interpChecks;interpOk&=mutate(x+offset,offset);}
  for(uint32_t offset:{0x9ecu,0x9f0u}){++interpChecks;interpOk&=mutate(x+offset,offset);}
  bool laserOk=mutate((uintptr_t)a->laser+0x42c,0x42c);
  float otherScale=1.f;
  for(auto t:timers){
   At<uint32_t>(x+0x528)=t.flag;auto before=Current(*a);
   At<uintptr_t>(x+t.offset+12)=(uintptr_t)&otherScale;auto after=Current(*a);
   pointerOk&=before.valid&&after.valid&&before.value==after.value;++pointerChecks;
   At<uintptr_t>(x+t.offset+12)=(uintptr_t)&a->scale;
  }
  At<uint32_t>(x+0x528)=0x2000000;auto before=Current(*a);
  At<uintptr_t>(x+0x9e4)=(uintptr_t)&otherScale;auto after=Current(*a);
  pointerOk&=before.valid&&after.valid&&before.value==after.value;++pointerChecks;
  At<uintptr_t>(x+0x9e4)=(uintptr_t)&a->scale;
  check(timerOk,0);check(operandOk,1);check(interpOk,2);check(laserOk,3);check(pointerOk,4);check(restoreOk,5);
  a->Init();b->Init();auto relocatedA=Current(*a),relocatedB=Current(*b);
  check(relocatedA.valid&&relocatedB.valid&&relocatedA.value==relocatedB.value,6);
  details[16]=timerChecks;details[17]=operandChecks;details[18]=interpChecks;
  details[19]=pointerChecks;details[20]=misses;details[21]=firstMiss;
 }else failure=0x20000000u;
 VirtualFree(b,0,MEM_RELEASE);VirtualFree(a,0,MEM_RELEASE);
 details[31]=failure;return failure;
}
}
#endif
