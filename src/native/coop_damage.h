#pragma once
#include "coop_damage_budget.h"
#include "coop_fpu.h"
#include <algorithm>

namespace coop_damage {
using coop_damage_budget::Unit;
constexpr uint32_t Limit=4096;
struct Task {
 CoopFpuState initialFpu,finishedFpu;
 void* fiber;
 unsigned char* data;
 int result,expectedHP;
 bool waiting,done,budgeted;
 uint32_t category,nativeAmount,bomb,applied,id;
 uint64_t shot[2],effective[2];
 uint64_t rawBomb[2];
};
struct Fraction {uint32_t id,remainder,lastFrame,category,live;};
struct GroupObservation {uint64_t shot[2],native,applied,bomb;uint32_t factor,targets;};
struct Observation {uint32_t magic,version,sequence,frame,activeMask,bossEpoch,phase[4],ufoEpoch,ufoId;GroupObservation groups[3];uint64_t totals[3][4];};
static_assert(sizeof(Observation)==288,"damage observation ABI");
static Observation observation;
static Observation* published;
static HANDLE observationMapping;
static Task tasks[Limit];
static Fraction fractions[Limit];
static coop_damage_budget::Window histories[3];
static coop_damage_budget::Ratio factors[3];
static uint32_t count,frame,phase[4];
static int previousBossHP;
static void *managerOriginal,*enemyOriginal,*scheduler;
static uintptr_t commitResume,damageSkip,clearResume,timerResume,timerSkip;
static Task* current;
static unsigned char* queryTarget;
static bool collecting,previousBoth,previousUfoActive,pendingBossReset;
static uint32_t previousUfoId;
template<class T> static T& At(uintptr_t p){return *reinterpret_cast<T*>(p);}
static void Fatal(){
 coop_lockstep::Fail(20);log("Damage frame batch could not be completed");
 // An incomplete batch must never silently run at unbounded firepower.
 TerminateProcess(GetCurrentProcess(),20);
}
static void Reset(){
 for(auto& h:histories)h.Clear();
 memset(fractions,0,sizeof(fractions));memset(phase,0,sizeof(phase));frame=0;
 previousBossHP=0;
 previousBoth=false;
 pendingBossReset=false;
 previousUfoActive=false;previousUfoId=0;
 memset(observation.totals,0,sizeof(observation.totals));
 observation.bossEpoch=0;
 observation.ufoEpoch=0;
}
static bool Tracing(){return current&&!current->waiting&&queryTarget==current->data;}
static bool AcceptShots(){return collecting&&Tracing()&&current->budgeted;}
static uint32_t RawAmount(){
 return coop_damage_sources::rawShot[0]+coop_damage_sources::rawShot[1]+coop_damage_sources::rawBomb[0]+coop_damage_sources::rawBomb[1];
}
static void Record(){
 if(!Tracing())return;
 for(int p=0;p<2;p++){
  current->shot[p]+=coop_campaign::ScaleDamage((int)coop_damage_sources::rawShot[p]);
  current->rawBomb[p]+=coop_campaign::ScaleDamage((int)coop_damage_sources::rawBomb[p]);
 }
}
// The native ECL extended-damage callback samples 31 points and compresses
// their sum. Its callback table is 4AF118 (0, 41AB80); it is not a UFO tag.
// Apply the same nonlinear transform to its bomb-only baseline, preserving
// bomb damage even when shots hit during the same callback.
static uint32_t Category(uintptr_t data){
 auto ufo=At<uintptr_t>(0x4b4534);
 auto full=At<uintptr_t>(data+0x174c);
 if(ufo&&At<uint32_t>(ufo+0x2c)&&At<uintptr_t>(ufo+0x34)==full&&At<uint32_t>(ufo+0x38)==At<uint32_t>(full+0x27b4))return 2;
 return (At<uint32_t>(data+0x16b8)&0x400000)?0:1;
}
static void NativeAmounts(Task& t){
 const bool ring=At<uintptr_t>((uintptr_t)t.data+0x176c)==coop_game::Code(0x41ab80);
 const auto spell=At<uintptr_t>(0x4b43cc),bomb=At<uintptr_t>(0x4b43c4);
 const uint32_t spellFlags=spell?At<uint32_t>(spell+0x7c):0;
 const int id=spell?At<int>(spell+0x78):-1;
 t.nativeAmount=t.bomb=0;
 for(int p=0;p<2;p++){
  auto actor=(uintptr_t)coop_resources::Actor(p);const int state=At<int>(actor+0xa28);
  const uint32_t flags=At<uint32_t>(actor+0xc414);
  const bool protectedActor=At<int>(actor+0xc404)!=0||(flags&2)!=0;
  const bool limited=(spellFlags&1)&&id>=93&&id<=99&&!(flags&4)&&((bomb&&At<int>(bomb+0x3c))||protectedActor);
  const bool thirtieth=(spellFlags&0x21)==0x21;
  auto limit=[&](uint64_t value){return coop_damage_budget::NativeAmount((uint32_t)value,ring,state==0||state==2,limited,thirtieth);};
  const uint32_t total=limit(t.shot[p]+t.rawBomb[p]),nativeBomb=limit(t.rawBomb[p]);
  t.nativeAmount+=total;t.bomb+=nativeBomb;t.effective[p]=(uint64_t)(total-nativeBomb)*Unit;
 }
}
static uint32_t __cdecl Commit(uintptr_t data,uint32_t amount){
 if(!current||current->data!=(unsigned char*)data||!amount)return amount;
 if((At<uint32_t>(data+0x16b8)&0x10)||At<int>(data+0x1694)>0)return amount;
 auto& t=*current;
 NativeAmounts(t);
 if(!t.budgeted||!t.nativeAmount)return t.nativeAmount;
 t.category=Category(data);
 t.id=At<uint32_t>(At<uintptr_t>(data+0x174c)+0x27b4);
 histories[t.category].Add(t.effective[0],t.effective[1]);
 t.waiting=true;current=nullptr;queryTarget=nullptr;SwitchToFiber(scheduler);
 return t.applied;
}
__declspec(naked) static void CommitHook(){__asm {
 pushfd
 pushad
 mov ebp,esp
 and esp,0fffffff0h
 sub esp,512
 fxsave [esp]
 fninit
 fldcw word ptr [esp]
 push esi
 push ebx
 call Commit
 add esp,8
 mov [ebp+4],eax
 fxrstor [esp]
 mov esp,ebp
 popad
 popfd
 test esi,esi
 jz noDamage
 test byte ptr [ebx+16b8h],10h
 jmp commitResume
noDamage:
 jmp damageSkip
}}
static void WINAPI RunTask(void* value){
 auto& t=*(Task*)value;
 for(;;){
  queryTarget=t.data;
  t.initialFpu.Restore();
  t.result=((int(__stdcall*)(void*))enemyOriginal)(t.data);
  t.finishedFpu.Save();t.done=true;
  current=nullptr;queryTarget=nullptr;SwitchToFiber(scheduler);
 }
}
static int __stdcall EnemyTick(unsigned char* data){
 if(!second||coop_characters::SinglePlayer())return ((int(__stdcall*)(void*))enemyOriginal)(data);
 const bool canonical=(uintptr_t)_ReturnAddress()==coop_game::Code(0x413199);
 if(!collecting||current||!canonical){
  Task trace{};trace.data=data;
  auto previousTarget=queryTarget;auto previousTask=current;queryTarget=data;current=&trace;
  int result=((int(__stdcall*)(void*))enemyOriginal)(data);
  queryTarget=previousTarget;current=previousTask;return result;
 }
 if(count==Limit){Fatal();return 1;}
 auto& t=tasks[count++];
 // Use the executable's stack reserve/commit defaults for native ECL calls.
 if(!t.fiber)t.fiber=CreateFiberEx(0,0,FIBER_FLAG_FLOAT_SWITCH,RunTask,&t);
 if(!t.fiber){Fatal();return 1;}
 auto fiber=t.fiber;t={};t.fiber=fiber;t.data=data;t.budgeted=true;current=&t;
 t.initialFpu.Save();
 SwitchToFiber(fiber);
 if(t.done)t.finishedFpu.Restore();
 // The native manager clears the per-frame visited flag after this returns.
 // Restore it before resuming a suspended native stack below.
 return t.done?t.result:0;
}
static void __cdecl ClearVisited(uintptr_t full){
 if(collecting&&count){
  auto& t=tasks[count-1];
  if(t.waiting&&!t.done&&t.data==(unsigned char*)(full+0x1040))return;
 }
 At<uint32_t>(full+0x26f8)&=~0x20000u;
}
__declspec(naked) static void ClearVisitedHook(){__asm {
 mov esi,[esi]
 pushfd
 pushad
 push esi
 call ClearVisited
 add esp,4
 popad
 popfd
 jmp clearResume
}}
// The manager's stage timer must remain unchanged while native enemy
// continuations are pending: their ECL/death handlers can read that timer.
__declspec(naked) static void TimerHook(){__asm {
 cmp collecting,0
 jne deferred
 mov esi,[esp+8]
 add esi,50h
 jmp timerResume
deferred:
 jmp timerSkip
}}
__declspec(noinline) static void TickTimer(void* timer){auto target=coop_game::Code(0x464a60);__asm {
 push esi
 mov esi,timer
 call target
 pop esi
}}
static Fraction& Remainder(uint32_t id,uint32_t category){
 // Search through deleted slots too: an enemy can stop receiving hits for
 // several frames without losing its fraction. Prune only dead native ids.
 uint32_t start=id%Limit;
 Fraction* vacant=nullptr;
 for(uint32_t n=0;n<Limit;n++){
  auto& r=fractions[(start+n)%Limit];
  if(r.live&&r.id==id){r.lastFrame=frame;return r;}
  if(!r.live&&!vacant)vacant=&r;
 }
 if(vacant){*vacant={id,0,frame,category,1};return *vacant;}
 Fatal();return fractions[0];
}
static void Prune(uintptr_t manager){
 uint32_t ids[Limit],used=0;auto node=At<uintptr_t>(manager+0x68);
 while(node&&used<Limit){
  auto full=At<uintptr_t>(node);if(full)ids[used++]=At<uint32_t>(full+0x27b4);
  node=At<uintptr_t>(node+4);
 }
 if(node){Fatal();return;}
 std::sort(ids,ids+used);
 for(auto& r:fractions)if(r.live&&!std::binary_search(ids,ids+used,r.id))r={};
}
static uint32_t BeginPhase(uintptr_t manager){
 uint32_t reset=0;
 // ECL setBoss stores Enemy* here, NOT EnemyData*. Verified at 417B09..23
 // and the native spell overlay's [boss+1074] read at 40DC30..39.
 const auto full=At<uintptr_t>(manager+0x1c),boss=full?full+0x1040:0,spell=At<uintptr_t>(0x4b43cc);
 uint32_t next[]={At<uint32_t>(0x4b0cb0),full?At<uint32_t>(full+0x27b4):0,
  boss?At<uint32_t>(boss+0x160c):0,spell?((At<uint32_t>(spell+0x7c)&1)?At<uint32_t>(spell+0x78)+1:0):0};
 if(next[0]!=phase[0]){for(auto& h:histories)h.Clear();memset(fractions,0,sizeof(fractions));++observation.bossEpoch;reset=7;}
 else if(pendingBossReset||memcmp(next,phase,sizeof(next))||(boss&&At<int>(boss+0x1608)>previousBossHP)){
  histories[0].Clear();for(auto& r:fractions)if(r.category==0)r={};
  ++observation.bossEpoch;
  reset|=1;
 }
 pendingBossReset=false;
 memcpy(phase,next,sizeof(next));
 previousBossHP=boss?At<int>(boss+0x1608):0;
 const auto ufo=At<uintptr_t>(0x4b4534);
 bool active=ufo&&At<uint32_t>(ufo+0x2c)&&At<uintptr_t>(ufo+0x34);
 uint32_t id=active?At<uint32_t>(ufo+0x38):0;
 if(active!=previousUfoActive||(active&&id!=previousUfoId)){
  histories[2].Clear();for(auto& r:fractions)if(r.category==2)r={};++observation.ufoEpoch;
  reset|=4;
 }
 previousUfoActive=active;previousUfoId=id;observation.ufoId=id;
 return reset;
}
static int __stdcall ManagerTick(void* manager){
 if(!second||coop_characters::SinglePlayer())return ((int(__stdcall*)(void*))managerOriginal)(manager);
 bool converted=!IsThreadAFiber();
 scheduler=converted?ConvertThreadToFiberEx(nullptr,FIBER_FLAG_FLOAT_SWITCH):GetCurrentFiber();
 if(!scheduler){Fatal();return 1;}
 if(!published){
  char name[64];sprintf_s(name,"Local\\TH12CoopDamage_%lu",GetCurrentProcessId());
  observationMapping=CreateFileMappingA(INVALID_HANDLE_VALUE,nullptr,PAGE_READWRITE,0,sizeof(Observation),name);
  if(observationMapping)published=(Observation*)MapViewOfFile(observationMapping,FILE_MAP_WRITE,0,0,sizeof(Observation));
 }
 memset(observation.groups,0,sizeof(observation.groups));
 ++frame;BeginPhase((uintptr_t)manager);
 const bool both=coop_resources::State(first)==1&&coop_resources::State(second)==1;
 observation.activeMask=(coop_resources::State(first)==1?1u:0u)|(coop_resources::State(second)==1?2u:0u);
 if(!both||!previousBoth)for(auto& h:histories)h.Clear();
 previousBoth=both;
 for(int c=0;c<3;c++)histories[c].Begin(c==0?60:30);
 count=0;current=nullptr;collecting=true;
 int result=((int(__stdcall*)(void*))managerOriginal)(manager);
 // ECL can start a phase, register a boss/UFO, or change actor state during
 // the native pre-damage work. Seal against that CURRENT phase, not the
 // previous frame's phase. Reinsert all same-frame accepted contributions.
 uint32_t reset=BeginPhase((uintptr_t)manager);
 const bool sealBoth=coop_resources::State(first)==1&&coop_resources::State(second)==1;
 observation.activeMask=(coop_resources::State(first)==1?1u:0u)|(coop_resources::State(second)==1?2u:0u);
 if(!sealBoth){for(auto& h:histories)h.Clear();reset=7;}
 for(uint32_t n=0;n<count;n++){
  const auto& t=tasks[n];
  if(t.waiting&&(reset&(1u<<t.category)))histories[t.category].Add(t.effective[0],t.effective[1]);
 }
 previousBoth=sealBoth;
 Prune((uintptr_t)manager);
 factors[0]=histories[0].Factor(6,5,sealBoth);factors[1]=histories[1].Factor(5,4,sealBoth);factors[2]=histories[2].Factor(1,1,sealBoth);
 for(uint32_t n=0;n<count;n++){
  auto& t=tasks[n];if(!t.waiting)continue;
  auto& fraction=Remainder(t.id,t.category);
  t.applied=t.bomb+coop_damage_budget::Apply(t.effective[0]+t.effective[1],factors[t.category],fraction.remainder);
  // EnemyLife::receive_damage, including its native seven-to-one mode.
  // A later ECL HP reset must invalidate the NEXT frame's phase history even
  // when it resets to the same max HP/spell id as the preceding attack.
  auto life=(uintptr_t)t.data+0x1608;
  if(!t.applied)t.expectedHP=At<int>(life);
  else if(At<uint32_t>(life+0x14)&1){
   int remaining=At<int>(life+0x0c)-(int)t.applied,base=At<int>(life+0x10);
   t.expectedHP=(remaining-base*7)/7+base;
  }else t.expectedHP=At<int>(life)-(int)t.applied;
  auto& group=observation.groups[t.category];
  for(int p=0;p<2;p++)group.shot[p]+=t.effective[p];
  group.native+=t.nativeAmount;group.applied+=t.applied;group.bomb+=t.bomb;++group.targets;
 }
 // Resume native HP, phase interrupts, hit effects and death handling in the
 // original list order, within the same EnemyManager tick. No extra game frame,
 // collision query, hit-score award or projectile consumption is introduced.
 for(uint32_t n=0;n<count;n++){
  auto& t=tasks[n];if(!t.waiting)continue;
  At<uint32_t>((uintptr_t)t.data+0x16b8)|=0x20000;
  current=&t;queryTarget=t.data;SwitchToFiber(t.fiber);
  if(!t.done){Fatal();break;}
  t.finishedFpu.Restore();
  if(t.result){
   auto full=At<uintptr_t>((uintptr_t)t.data+0x174c);
   auto destroy=At<uintptr_t>(At<uintptr_t>(full)+0x14);
   ((void(__thiscall*)(void*,int))destroy)((void*)full,1);
  }else At<uint32_t>((uintptr_t)t.data+0x16b8)&=~0x20000u;
 }
 collecting=false;current=nullptr;queryTarget=nullptr;scheduler=nullptr;
 TickTimer((unsigned char*)manager+0x50);
 auto boss=At<uintptr_t>((uintptr_t)manager+0x1c);
 previousBossHP=boss?At<int>(boss+0x1040+0x1608):0;
 if(boss)for(uint32_t n=0;n<count;n++){
  const auto& t=tasks[n];
  if(t.waiting&&t.category==0&&t.id==At<uint32_t>(boss+0x27b4)&&previousBossHP>t.expectedHP)pendingBossReset=true;
 }
 observation.magic=0x44323154;observation.version=1;++observation.sequence;observation.frame=frame;
 memcpy(observation.phase,phase,sizeof(phase));
 for(int c=0;c<3;c++){
  auto& g=observation.groups[c];g.factor=(uint32_t)coop_damage_budget::MultiplyDivide(Unit,factors[c]);
  observation.totals[c][0]+=g.native;observation.totals[c][1]+=g.applied;
  observation.totals[c][2]+=g.bomb;observation.totals[c][3]+=g.targets;
 }
 if(published)memcpy(published,&observation,sizeof(observation));
 if(converted&&!ConvertFiberToThread())Fatal();
 return result;
}
static void Append(coop_lockstep::Hash& h){
 h.word(frame);h.word(previousBoth);h.word(previousUfoActive);h.word(previousUfoId);h.word(pendingBossReset);h.word((uint32_t)previousBossHP);for(auto value:phase)h.word(value);
 for(const auto& history:histories){h.word(history.cursor);for(const auto& f:history.frames)for(auto v:f){h.word((uint32_t)v);h.word((uint32_t)(v>>32));}}
 for(const auto& r:fractions)if(r.live){h.word(r.id);h.word(r.remainder);h.word(r.lastFrame);h.word(r.category);}
 // Provenance survives the asynchronous effects which created these sources.
 for(const auto& player:coop_damage_sources::bomb)for(bool value:player)h.word(value);
}
static void Register(coop_patch::Registry& r){
 const unsigned char manager[]={0x8b,0x44,0x24,0x04,0x56};
 const unsigned char enemy[]={0x55,0x8b,0xec,0x83,0xe4,0xf8};
 const unsigned char commit[]={0xf6,0x83,0xb8,0x16,0,0,0x10};
 const unsigned char clear[]={0x8b,0x36,0x81,0xa6,0xf8,0x26,0,0,0xff,0xff,0xfd,0xff};
 const unsigned char timer[]={0x8b,0x74,0x24,0x08,0x83,0xc6,0x50};
 commitResume=coop_game::Code(0x413e67);
 damageSkip=coop_game::Code(0x413f3d);
 clearResume=coop_game::Code(0x4131a9);
 timerResume=coop_game::Code(0x4131c8);timerSkip=coop_game::Code(0x4131cd);
 r.Hook100a(0x413170,manager,sizeof(manager),(void*)ManagerTick,&managerOriginal);
 r.Hook100a(0x413820,enemy,sizeof(enemy),(void*)EnemyTick,&enemyOriginal);
 r.Hook100a(0x413e60,commit,sizeof(commit),(void*)CommitHook);
 r.Hook100a(0x41319d,clear,sizeof(clear),(void*)ClearVisitedHook);
 r.Hook100a(0x4131c1,timer,sizeof(timer),(void*)TimerHook);
}
}
