#pragma once
#ifdef COOP_SMOKE_TEST
#include "coop_effects.h"
// Destructive, main-thread-only fixtures. Start a fresh game process for every
// case and shot type, run when both actors are state1, record, then exit. Native
// effects/VMs/items are deliberately not rolled back by copying object memory.
namespace coop_resource_audit {
using namespace coop_resources;
static void Timer(unsigned char* p,int value){SetStateTimer(p,value);}
static void Tick(unsigned char* p,uint32_t input=0){
 auto old=Player();Player()=p;uint32_t saved[5];memcpy(saved,(void*)0x4d49d0,sizeof(saved));
 uint32_t controls[5]={input,0,input,input,0};memcpy((void*)0x4d49d0,controls,sizeof(controls));
 BeforeTick(p);((int(__stdcall*)(void*))coop_game::Code(0x436b80))(p);AfterTick(p);
 memcpy((void*)0x4d49d0,saved,sizeof(saved));Player()=old;
}
__declspec(noinline) static void Die(unsigned char* p){auto target=coop_game::Code(0x4381c0);__asm {
 mov esi,p
 call target
 }}
__declspec(noinline) static void Extend(){auto target=coop_game::Code(0x422cc0);__asm {
 mov eax,0x4b0c40
 call target
 }}
static void ClearDamage(unsigned char* p){
 for(int i=0;i<256;i++)At<int>((uintptr_t)p+0xa58+i*0x78+0x48)=0;
 for(int i=0;i<128;i++)At<uint32_t>((uintptr_t)p+0x8988+i*0x74+0x70)=0;
}
__declspec(noinline) static void Source(unsigned char* p,float* pos,int amount){
 auto target=coop_game::Code(0x4390d0);float radius=64.f,angle=0.f;
 __asm {
 push amount
 push 61
 push angle
 push radius
 push pos
 mov eax,p
 call target
 }
}
__declspec(noinline) static int RawBomb(float* pos,float* box){int value;__asm {
 push box
 mov edi,pos
 call bombDamageOriginal
 mov value,eax
 }return value;}
__declspec(noinline) static void BombTick(unsigned char* bomb){auto target=coop_game::Code(0x406cc0);__asm {
 mov eax,bomb
 call target
 }}
static uint32_t SourceCount(unsigned char* p){uint32_t count=0;
 for(int i=0;i<128;i++)if(At<uint32_t>((uintptr_t)p+0x8988+i*0x74+0x70)&1)++count;
 return count;
}
// 0x401610 precedes the 1.00b insertion and has the same address in all builds.
// Prime its 256-byte scratch buffer with a bounded invalid result. This makes
// the 1.00a missing-format branch observable without relying on a random crash.
__declspec(noinline) static uint32_t FormatScore(int value,float* position){
 __try {
  __asm {
   sub esp,0x108
   mov edi,esp
   mov ecx,0x100
   mov al,0x3f
   rep stosb
   mov byte ptr [esp+0xff],0
   add esp,0x108
   mov ebx,position
   mov ecx,value
   mov eax,0x401610
   call eax
  }
  return 0;
 } __except(EXCEPTION_EXECUTE_HANDLER) {return GetExceptionCode();}
}
}
static uint32_t RunResourceAudit(uint32_t caseId,uint32_t* details){
 using namespace coop_resources;
 using namespace coop_resource_audit;
 if(!details)return 0x80000000u;
 memset(details,0,32*sizeof(uint32_t));details[0]=caseId;
 if(!enabled||!firstActor||!secondActor||State(firstActor)!=1||State(secondActor)!=1)return 0x80000000u;
 auto bomb=At<unsigned char*>(0x4b43c4);auto spell=At<unsigned char*>(0x4b43cc);
 auto enemy=At<unsigned char*>(0x4b43dc);auto gui=At<unsigned char*>(0x4b43e4);auto pause=At<unsigned char*>(0x4b4510);
 if(!bomb||!spell||!enemy||!gui||!pause||At<int>((uintptr_t)bomb+0x3c)!=0)return 0x40000000u;
 uint32_t failure=0;auto check=[&](bool condition,unsigned bit){if(!condition)failure|=1u<<bit;};
 OnActivate();At<uint32_t>((uintptr_t)spell+0x7c)&=~1u;
 At<float>(0x4b2ed0)=1.f;
 for(int n=0;n<2;n++){auto p=Actor(n);Timer(p,60);At<uint32_t>((uintptr_t)p+0xc414)&=~7u;ClearDamage(p);}
 if(caseId==0){
  At<int>(0x4b0c98)=1;At<int>(0x4b0ca0)=0;
  Die(firstActor);check(At<int>(0x4b0c98)==0&&credit[0]&&State(firstActor)==2,0);
  Die(secondActor);check(At<int>(0x4b0c98)==0&&!credit[1]&&State(secondActor)==2,1);
  Timer(firstActor,30);Tick(firstActor);check(State(firstActor)==0&&!credit[0]&&!gameOverIssued,2);
  Timer(secondActor,30);Tick(secondActor);check(State(secondActor)==2&&!gameOverIssued,3);
  check(At<int>(0x4b0ca0)==0,4); // Neither miss can replenish shared bombs.
  Extend();BeforeTick(firstActor);check(credit[1]&&At<int>(0x4b0c98)==0,5);
  Tick(secondActor);check(State(secondActor)==0&&!credit[1]&&!gameOverIssued,6);
  details[1]=At<uint32_t>(0x4b0c98);details[2]=At<uint32_t>(0x4b0ca0);
 }else if(caseId==1){
  At<int>(0x4b0c98)=0;Die(firstActor);Timer(firstActor,30);Tick(firstActor);
  check(State(firstActor)==2&&State(secondActor)==1&&!gameOverIssued,0);
  Die(secondActor);Timer(secondActor,29);Timer(firstActor,30);Tick(firstActor);
  check(!gameOverIssued,1);
  Timer(secondActor,30);Tick(secondActor);check(gameOverIssued&&At<int>(0x4b0c98)==0,2);
  auto gm=At<unsigned char*>(0x4b44e8);check(gm&&(At<uint32_t>((uintptr_t)gm+0x60)&0x10)!=0,3);
  At<int>((uintptr_t)pause+0x14)=123;Tick(firstActor);
  check(At<int>((uintptr_t)pause+0x14)==123,4); // Second gate must not restart game-over.
  details[1]=At<uint32_t>((uintptr_t)pause+0x14);details[2]=gameOverIssued;
 }else if(caseId==2){
  At<int>(0x4b0ca0)=2;
  for(int n=0;n<2;n++){auto p=Actor(n);At<int>((uintptr_t)p+0xa28)=4;Timer(p,0);Invulnerable(p,0);}
  Tick(firstActor,2);Tick(secondActor,2);
  check(State(firstActor)==1&&State(secondActor)==1,0);
  check(At<int>((uintptr_t)bomb+0x3c)==1&&bombActor==0,1);
  check(At<int>(0x4b0ca0)==1,2);
  check(At<int>((uintptr_t)firstActor+0xc404)>0&&At<int>((uintptr_t)secondActor+0xc404)>0,3);
  Invulnerable(secondActor,280);Player()=firstActor;
  ((void(__stdcall*)(int))coop_game::Code(0x406f40))(40);
  check(At<int>((uintptr_t)firstActor+0xc404)==40&&At<int>((uintptr_t)secondActor+0xc404)==280,4);
  details[1]=At<uint32_t>(0x4b0ca0);details[2]=At<uint32_t>((uintptr_t)firstActor+0xc404);details[3]=At<uint32_t>((uintptr_t)secondActor+0xc404);
 }else if(caseId==3){
  float pos[3]={0,100,0},box[2]={16,16};Source(firstActor,pos,10);Source(secondActor,pos,20);
  auto original=(int(__stdcall*)(void*,void*))damageOrig;
  Player()=firstActor;int a=original(pos,box);Player()=secondActor;int b=original(pos,box);
  check(a==10&&b==20,0);details[1]=a;details[2]=b;
  int both=Damage(pos,box,damageOrig);check(both==30,1);details[3]=both;
  At<int>((uintptr_t)firstActor+0xa28)=0;
  int entry=Damage(pos,box,damageOrig);check(entry==22,2);details[4]=entry;
  At<int>((uintptr_t)firstActor+0xa28)=2;
  int missed=Damage(pos,box,damageOrig);check(missed==22,3);details[5]=missed;
  At<int>((uintptr_t)firstActor+0xa28)=1;
  At<uint32_t>((uintptr_t)spell+0x7c)|=1;At<int>((uintptr_t)spell+0x78)=93;
  Invulnerable(firstActor,40);Invulnerable(secondActor,0);
  int protectedDamage=Damage(pos,box,damageOrig);check(protectedDamage==22,4);details[6]=protectedDamage;
 }else if(caseId==4){
  Player()=secondActor;int start=((int(__cdecl*)())coop_game::Code(0x406bd0))();
  check(start==0&&bombActor==1,0);
  ClearDamage(firstActor);ClearDamage(secondActor);
  At<int>((uintptr_t)bomb+0x14)=99;At<int>((uintptr_t)bomb+0x18)=100;At<float>((uintptr_t)bomb+0x1c)=100.f;
  float pos[3]={0,100,0},box[2]={2000,2000};int native=RawBomb(pos,box);
  int combined=Damage(pos,box,damageOrig);check(combined==native,1);
  int shot=At<int>(0x4b0c90)*2+At<int>(0x4b0c94);
  check((shot==2||shot==3||shot==4)?native>0:native==0,2);
  details[1]=shot;details[2]=native;details[3]=combined;details[4]=bombActor;
 }else if(caseId==5){
  At<int>((uintptr_t)gui+0x6d30)=0;At<int>((uintptr_t)enemy+0x70)=1;
  At<int>(0x4b0ccc)=0;Timer(firstActor,59);Timer(secondActor,59);
  Tick(firstActor);int one=At<int>(0x4b0ccc);Tick(secondActor);int two=At<int>(0x4b0ccc);
  check(one==1&&two==1,0);details[1]=one;details[2]=two;
 }else if(caseId==6){
  if(At<int>(0x4b0c90)!=0||At<int>(0x4b0c94)!=1)return 0x10000000u;
  Player()=secondActor;int start=((int(__cdecl*)())coop_game::Code(0x406bd0))();
  check(start==0&&bombActor==1,0);
  BombTick(bomb); // At native bomb timer0, create the eight Reimu B orbs.
  uint32_t before1=SourceCount(firstActor),before2=SourceCount(secondActor);
  Player()=firstActor;((void(__cdecl*)())coop_game::Code(0x406eb0))();
  uint32_t after1=SourceCount(firstActor),after2=SourceCount(secondActor);
  check(before1==0&&before2==8,1);
  check(after1==0&&after2==8,2); // Eight orbs retire and eight owner explosions replace them.
  check(At<int>((uintptr_t)bomb+0x3c)==0&&Player()==firstActor,3);
  details[1]=before1;details[2]=before2;details[3]=after1;details[4]=after2;
 }else if(caseId==7){
  if(At<int>(0x4b0c90)!=0||At<int>(0x4b0c94)!=0)return 0x10000000u;
  At<int>(0x4b0ca0)=2;Player()=secondActor;
  check(((int(__cdecl*)())coop_game::Code(0x406bd0))()==0&&bombActor==1,0);
  uintptr_t emitter=0,child=0;
  coop_effects::Visit([&](uintptr_t vm,uintptr_t p,bool isEmitter){
   if(isEmitter&&At<uint32_t>(p+0x30)==2&&At<uint32_t>(p+0x34)==coop_effects::generation)emitter=vm;
  });
  check(emitter!=0,1);if(!emitter)return failure;
  auto emit=(int(__fastcall*)(unsigned char*,void*))coop_game::Code(0x46a2e0);
  auto beam=(int(__fastcall*)(unsigned char*,void*))coop_game::Code(0x469d20);
  Player()=firstActor;emit((unsigned char*)emitter,nullptr);
  details[1]=SourceCount(firstActor);details[2]=SourceCount(secondActor);
  check(details[1]==0&&details[2]==2&&Player()==firstActor,2);
  uint32_t children=0;
  coop_effects::Visit([&](uintptr_t vm,uintptr_t p,bool isEmitter){
   if(!isEmitter&&At<uint32_t>(p+0x41c)==coop_effects::generation){
    ++children;child=vm;check(At<uint32_t>(p+0x418)==2,3);
   }
  });
  check(children==2&&child!=0,4);if(!child)return failure;
  // Stress the persistent latch independently of the mutable Bomb owner.
  // RA has no native cancellation entry; this is an explicit fixture mutation,
  // not a claim that overlapping RA bombs can occur during ordinary play.
  bombActor=0;
  auto payload=At<uintptr_t>(emitter+0x478);
  At<int>(payload+0xc)=3;At<int>(payload+0x10)=4;At<float>(payload+0x14)=4.f;
  emit((unsigned char*)emitter,nullptr);
  details[3]=SourceCount(firstActor);details[4]=SourceCount(secondActor);
  check(details[3]==0&&details[4]==4&&Player()==firstActor&&bombActor==0,5);
  coop_effects::Reset(true);
  check(SourceCount(firstActor)==0&&SourceCount(secondActor)==0,7);
  details[5]=emit((unsigned char*)emitter,nullptr);
  auto childPayload=At<uintptr_t>(child+0x478),source=At<uintptr_t>(childPayload+0x414);
  At<uintptr_t>(childPayload+0x414)=1; // Retired generation must not dereference this.
  details[6]=beam((unsigned char*)child,nullptr);
  At<uintptr_t>(childPayload+0x414)=source;
  check(details[5]==1&&details[6]==1&&Player()==firstActor,8);
 }else if(caseId>=8&&caseId<=11){
  // Real native Player ticks in production P1 -> P2 order. Cases8/9 are
  // mirrored final-valid-frame deathbombs; cases10/11 reject expired partners.
  At<int>(0x4b0c98)=2;At<int>(0x4b0ca0)=2;
  At<int>((uintptr_t)gui+0x6d30)=0;At<int>((uintptr_t)enemy+0x70)=1;
  int initiator=caseId==8||caseId==10?1:0;
  int expired=1-initiator;
  for(int n=0;n<2;n++){
   auto p=Actor(n);At<int>((uintptr_t)p+0xa28)=caseId<10||n==expired?4:1;
   Timer(p,caseId<10?7:n==expired?8:60);Invulnerable(p,0);
  }
  Tick(firstActor,initiator==0?2:0);Tick(secondActor,initiator==1?2:0);
  details[1]=State(firstActor);details[2]=State(secondActor);
  details[3]=At<uint32_t>(0x4b0c98);details[4]=At<uint32_t>(0x4b0ca0);details[5]=bombActor;
  if(caseId<10){
   check(State(firstActor)==1&&State(secondActor)==1,0);
   check(At<int>(0x4b0c98)==2,1);
  }else{
   check(State(Actor(expired))==2&&State(Actor(initiator))==1,0);
   check(At<int>(0x4b0c98)==1&&credit[expired],1);
  }
  check(At<int>(0x4b0ca0)==1&&bombActor==initiator,2);
  Tick(firstActor);Tick(secondActor);
  if(caseId<10)check(State(firstActor)==1&&State(secondActor)==1&&At<int>(0x4b0c98)==2,3);
  details[6]=State(firstActor);details[7]=State(secondActor);details[8]=At<uint32_t>(0x4b0c98);
 }else if(caseId==12){
  // Isolate only the native text queue; no synthetic score is saved. Calls
  // below still execute the real formatter and its real queue insertion.
  auto ascii=(unsigned char*)VirtualAlloc(nullptr,0x20000,MEM_COMMIT|MEM_RESERVE,PAGE_READWRITE);
  if(!ascii)return 0x40000001u;
  auto oldAscii=At<unsigned char*>(0x4b43b8);At<unsigned char*>(0x4b43b8)=ascii;
  const int values[]={-1,0,999,1000,999999999,1000000000,1000000001,2147483647};
  const char* expected[]={"-1","0","999","1,000","999,999,999","1,000,000,000","1,000,000,001","2,147,483,647"};
  float position[3]={0,0,0};
  for(unsigned n=0;n<8;n++){
   memset(ascii,0,0x20000);
   uint32_t exception=FormatScore(values[n],position);
   const char* actual=(const char*)ascii+0x97c;
   check(exception==0&&At<uint32_t>((uintptr_t)ascii+0x18f7c)==1&&strcmp(actual,expected[n])==0,n);
   details[1+n*3]=exception;details[2+n*3]=At<uint32_t>((uintptr_t)ascii+0x18f7c);
   memcpy(details+3+n*3,actual,4);
  }
  At<unsigned char*>(0x4b43b8)=oldAscii;VirtualFree(ascii,0,MEM_RELEASE);
 }else return 0x20000000u;
 Player()=firstActor;details[28]=State(firstActor);details[29]=State(secondActor);
 details[30]=(uint32_t)OwnStateHash();details[31]=failure;return failure;
}
#endif
