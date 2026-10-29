#pragma once
#include "patch.h"

// Included after the two actors, previous[] and mem/singleton declarations.
// Native ItemManager still advances and awards each slot exactly once.
namespace coop_items {
using Eligible = bool (*)(unsigned char*);
constexpr size_t Count=2664, Stride=0x9d8;
static unsigned char owners[Count]; // 0=unclaimed, 1=P1, 2=P2; never pointers.
static unsigned char* manager;
static unsigned char* pending;
static size_t pendingIndex;
static unsigned char pendingOwner;
static bool running, noCollector, pickupAllowed=true;
static Eligible actorEligible;
static uint32_t masks[2];
static uintptr_t pickupContinue,pickupSkip,pocContinue,pocSkip;
static uintptr_t apexContinue,apexSkip,tokenContinue,tokenSkip;

static int State(unsigned char* item){return ::mem<int>((uintptr_t)item+0x9b0);}
static bool Homing(int state){return state==3||state==4||state==8;}
static unsigned char* Actor(unsigned char id){return id==1?::first:id==2?::second:nullptr;}
static unsigned char Id(unsigned char* p){return p==::first?1:p==::second?2:0;}
static bool EligibleActor(unsigned char* p){
 if(!p)return false;
 int state=::mem<int>((uintptr_t)p+0xa28);
 return (state==0||state==1)&&(!actorEligible||actorEligible(p));
}
static float Distance(unsigned char* item,unsigned char* p){
 float x=::mem<float>((uintptr_t)item+0x96c)-::mem<float>((uintptr_t)p+0x97c);
 float y=::mem<float>((uintptr_t)item+0x970)-::mem<float>((uintptr_t)p+0x980);
 return x*x+y*y;
}
static unsigned char* Closest(unsigned char* item,bool a,bool b){
 if(!a)return b?::second:nullptr;
 if(!b)return ::first;
 return Distance(item,::second)<Distance(item,::first) ? ::second : ::first;
}
static bool Contains(unsigned char* item,unsigned char* p,size_t bounds){
 const auto a=(uintptr_t)p;
 const float x=::mem<float>((uintptr_t)item+0x96c),y=::mem<float>((uintptr_t)item+0x970);
 return x>=::mem<float>(a+bounds)&&y>=::mem<float>(a+bounds+4)&&
        x<=::mem<float>(a+bounds+12)&&y<=::mem<float>(a+bounds+16);
}
static bool AboveLine(unsigned char* p){return ::mem<float>((uintptr_t)p+0x980)<::mem<float>(0x4a3db8);}
static void Context(unsigned char* p){
 if(!p)return;
 ::singleton()=p;
 // The native item pass reads only current focus, at 0x426DEB (1.00a).
 ::mem<uint32_t>(GAME_INPUT)=masks[p==::second?1:0];
}
static void FinishItem(){
 if(!pending)return;
 owners[pendingIndex]=Homing(State(pending))?pendingOwner:0;
 pending=nullptr;
}
static void Reset(){
 memset(owners,0,sizeof(owners));manager=nullptr;pending=nullptr;
 pendingOwner=0;running=false;noCollector=false;pickupAllowed=true;
}
static uint64_t DeterministicStateHash(){
 uint64_t hash=14695981039346656037ull;
 for(auto owner:owners){hash^=owner;hash*=1099511628211ull;}
 return hash;
}
static void __cdecl SelectForMotion(unsigned char* item){
 if(!running)return;
 FinishItem();
 const auto delta=(uintptr_t)item-((uintptr_t)manager+0x14);
 if(delta%Stride||delta/Stride>=Count)return;
 pending=item;pendingIndex=delta/Stride;
 int state=State(item);
 // Native spawn resets the item timer even when a slot is recycled between
 // two manager passes (including forced-homing UFO rewards in state 8).
 if(::mem<int>((uintptr_t)item+0x98c)==0)owners[pendingIndex]=0;
 bool a=EligibleActor(::first),b=EligibleActor(::second);
 noCollector=!a&&!b;
 auto chosen=Actor(owners[pendingIndex]);
 if(!Homing(state)||!EligibleActor(chosen))chosen=Closest(item,a,b);
 if(!Homing(state))owners[pendingIndex]=0;
 if(state==1){
  // Any active actor above the line attracts ordinary falling items, even
  // when an actor below the line happens to be nearer to that item.
  auto above=Closest(item,a&&AboveLine(::first),b&&AboveLine(::second));
  if(above)chosen=above;
 }
 if(Homing(state)&&noCollector){
  // No living target: release attraction and retain the ordinary fall. The
  // native POC/apex guards below prevent reacquiring a waiting/dead actor.
  ::mem<int>((uintptr_t)item+0x9b0)=1;
  ::mem<float>((uintptr_t)item+0x978)=0.f;
  ::mem<float>((uintptr_t)item+0x97c)=0.f;
  owners[pendingIndex]=0;
 }else if(state==4&&((a&&AboveLine(::first))||(b&&AboveLine(::second)))){
  // Upgrading local magnet attraction preserves its owner and full-value
  // auto-collection semantics. Native full homing uses SHT float +8.
  const auto shot=::mem<uintptr_t>((uintptr_t)chosen+0xa2c);
  ::mem<int>((uintptr_t)item+0x9b0)=3;
  ::mem<float>((uintptr_t)item+0x9bc)=::mem<float>(shot+8);
 }
 pendingOwner=Id(chosen);
 Context(chosen ? chosen : ::first);
}
static void __cdecl SelectForPickup(unsigned char* item){
 pickupAllowed=true;
 if(!running)return;
 bool a=EligibleActor(::first),b=EligibleActor(::second);
 auto chosen=Closest(item,a&&Contains(item,::first,0xc444),b&&Contains(item,::second,0xc444));
 if(!chosen&&!Homing(State(item))&&State(item)!=6&&State(item)!=7){
  // Check the union of the actors' actual focus-dependent rectangles, not
  // the rectangle of whichever actor happens to have the shortest distance.
  chosen=Closest(item,a&&Contains(item,::first,(masks[0]&8)?0xc45c:0xc474),
                      b&&Contains(item,::second,(masks[1]&8)?0xc45c:0xc474));
  if(chosen)pendingOwner=Id(chosen);
 }
 if(!chosen){chosen=Actor(pendingOwner);if(!EligibleActor(chosen))chosen=Closest(item,a,b);}
 pickupAllowed=chosen!=nullptr;
 Context(chosen);
}
static int Tick(void* p,void* d,void* original,Eligible eligible=nullptr){
 auto fn=(int(__fastcall*)(void*,void*))original;
 if(!::first||!::second)return fn(p,d);
 if(manager!=p){Reset();manager=(unsigned char*)p;}
 uint32_t saved[5];memcpy(saved,(void*)GAME_INPUT,sizeof(saved));
 auto old=::singleton();actorEligible=eligible;
 // Player priority 16 precedes ItemManager priority 22. previous[] holds the
 // exact controls just consumed by each actor, including committed net input.
 masks[0]=::previous[0];masks[1]=::previous[1];running=true;
 int result=fn(p,d);
 FinishItem();running=false;noCollector=false;pickupAllowed=true;
 ::singleton()=old;memcpy((void*)GAME_INPUT,saved,sizeof(saved));
 return result;
}
__declspec(naked) static void PickupHook(){__asm {
 pushfd
 pushad
 push edi
 call SelectForPickup
 add esp,4
 popad
 popfd
 cmp pickupAllowed,0
 je skipPickup
 mov ecx,dword ptr ds:[4b4514h]
 jmp pickupContinue
 skipPickup:
 jmp pickupSkip
 }}
__declspec(naked) static void PocHook(){__asm {
 cmp noCollector,0
 jne skipPoc
 mov ecx,dword ptr ds:[4b4514h]
 jmp pocContinue
 skipPoc:
 jmp pocSkip
 }}
__declspec(naked) static void ApexHook(){__asm {
 cmp noCollector,0
 jne skipApex
 mov edx,dword ptr ds:[4b4514h]
 jmp apexContinue
 skipApex:
 jmp apexSkip
 }}
__declspec(naked) static void TokenHook(){__asm {
 cmp noCollector,0
 jne skipToken
 mov eax,dword ptr ds:[4b4514h]
 jmp tokenContinue
 skipToken:
 jmp tokenSkip
 }}
static void Register(coop_patch::Registry& registry){
 const unsigned char ecx[]={0x8b,0x0d,0x14,0x45,0x4b,0x00};
 const unsigned char edx[]={0x8b,0x15,0x14,0x45,0x4b,0x00};
 const unsigned char eax[]={0xa1,0x14,0x45,0x4b,0x00};
 pickupContinue=coop_game::Code(0x426095);pickupSkip=coop_game::Code(0x426eab);
 pocContinue=coop_game::Code(0x425cbc);pocSkip=coop_game::Code(0x425d16);
 apexContinue=coop_game::Code(0x425e82);apexSkip=coop_game::Code(0x425e9d);
 tokenContinue=coop_game::Code(0x426274);tokenSkip=coop_game::Code(0x42630d);
 registry.Hook100a(0x42608f,ecx,sizeof(ecx),(void*)PickupHook);
 registry.Hook100a(0x425cb6,ecx,sizeof(ecx),(void*)PocHook);
 registry.Hook100a(0x425e7c,edx,sizeof(edx),(void*)ApexHook);
 registry.Hook100a(0x42626f,eax,sizeof(eax),(void*)TokenHook);
}
}
