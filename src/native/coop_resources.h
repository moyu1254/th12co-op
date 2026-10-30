#pragma once
#include <windows.h>
#include <stdint.h>
#include "patch.h"

// Native addresses are for retail 1.00a; Registry applies the verified 1.00b
// code shift. Object offsets/globals are identical in all supported profiles.
namespace coop_resources {
static unsigned char *firstActor, *secondActor;
static bool enabled, credit[2], gameOverIssued, suppressBomb;
// Transient paired-tick snapshot: P1 and P2 observe the same deathbomb deadline
// even though native Player ticks run in that fixed order.
static bool frameOpen, graceAtFrameStart[2];
static int bombActor=-1;
static void *deathOriginal, *bombStartOriginal, *invulnOriginal, *bombDamageOriginal, *bombCancelOriginal;
static uintptr_t respawnAddress, waitAddress, gameOverAddress, rankResume, damageScaleResume, damageScaleSkip, spellScaleResume, spellScaleSkip;
template<class T> static T& At(uintptr_t p){return *reinterpret_cast<T*>(p);}
static unsigned char*& Player(){return At<unsigned char*>(0x4b4514);}
static unsigned char* Actor(int n){return n==0?firstActor:secondActor;}
static int Index(unsigned char* p){return p==firstActor&&p?0:p==secondActor&&p?1:-1;}
static int State(unsigned char* p){return At<int>((uintptr_t)p+0xa28);}
static bool Eligible(unsigned char* p){return p&&!gameOverIssued&&(State(p)==0||State(p)==1);}
static void Reset(unsigned char* p1=nullptr,unsigned char* p2=nullptr){
 firstActor=p1;secondActor=p2;enabled=p1&&p2;
 credit[0]=credit[1]=gameOverIssued=suppressBomb=false;bombActor=-1;
 frameOpen=graceAtFrameStart[0]=graceAtFrameStart[1]=false;
}
static void OnActivate(){
 credit[0]=credit[1]=gameOverIssued=false;bombActor=-1;
 frameOpen=graceAtFrameStart[0]=graceAtFrameStart[1]=false;
}
static void UpdateLives(){
 auto gui=At<void*>(0x4b43e4);
 if(gui)((void(__stdcall*)(void*,int,int))coop_game::Code(0x41ce40))(gui,At<int>(0x4b0c98),At<int>(0x4b0c9c));
}
// Pickups enter the one native reserve counter. Allocate new reserves to
// waiting actors before either actor can spend them on another miss.
static void AllocateReserves(){
 if(!enabled||gameOverIssued)return;
 bool changed=false;
 for(int n=0;n<2;n++)if(State(Actor(n))==2&&!credit[n]&&At<int>(0x4b0c98)>0){
  --At<int>(0x4b0c98);credit[n]=true;changed=true;
 }
 if(changed)UpdateLives();
}
static void BeforeTick(unsigned char* p){
 if(p!=firstActor)return;
 AllocateReserves();frameOpen=enabled;
 for(int n=0;n<2;n++)graceAtFrameStart[n]=enabled&&State(Actor(n))==4&&At<int>((uintptr_t)Actor(n)+0xa34)<8;
}
static void AfterTick(unsigned char* p){
 if(p==secondActor)frameOpen=graceAtFrameStart[0]=graceAtFrameStart[1]=false;
}
__declspec(noinline) static void CallDeath(unsigned char* actor){__asm {
 mov esi,actor
 call deathOriginal
 }}
static void __cdecl CommitDeath(unsigned char* actor){
 int n=Index(actor);
 if(!enabled||n<0){CallDeath(actor);return;}
 credit[n]=At<int>(0x4b0c98)>0;
 CallDeath(actor); // Native effects, spell failure, rank penalty and death timer.
 if(At<int>(0x4b0c98)<0){At<int>(0x4b0c98)=0;UpdateLives();}
}
__declspec(naked) static void DeathHook(){__asm {
 pushad
 push esi
 call CommitDeath
 add esp,4
 popad
 ret
 }}
// 1=original respawn, 0=wait invisibly in native state2, -1=original game-over.
static int __cdecl RespawnDecision(unsigned char* actor){
 if(!enabled)return At<int>(0x4b0c98)>=0?1:-1;
 int n=Index(actor);
 if(n<0||gameOverIssued)return 0;
 AllocateReserves();
 if(credit[n]){credit[n]=false;return 1;}
 for(int i=0;i<2;i++)if(credit[i]||State(Actor(i))!=2||At<int>((uintptr_t)Actor(i)+0xa34)<30)return 0;
 gameOverIssued=true;return -1;
}
__declspec(naked) static void RespawnHook(){__asm {
 pushad
 push edi
 call RespawnDecision
 add esp,4
 cmp eax,1
 je respawn
 test eax,eax
 jl gameover
 popad
 xor ebx,ebx
 jmp waitAddress
 respawn:
 popad
 xor ebx,ebx
 jmp respawnAddress
 gameover:
 popad
 xor ebx,ebx
 jmp gameOverAddress
 }}
__declspec(naked) static void RankHook(){__asm {
 mov eax,dword ptr ds:[0x4b0ccc]
 cmp edi,secondActor
 je done
 inc eax
 done:
 jmp rankResume
 }}
__declspec(noinline) static void SetStateTimer(unsigned char* actor,int frames){
 auto timer=actor+0xa30;auto target=coop_game::Code(0x4067c0);
 __asm {
 push frames
 mov eax,timer
 call target
 }
}
static void Invulnerable(unsigned char* actor,int frames){
 auto old=Player();Player()=actor;
 ((void(__stdcall*)(int))invulnOriginal)(frames);
 Player()=old;
}
static void __stdcall InvulnerabilityHook(int frames){
 auto owner=Player();
 ((void(__stdcall*)(int))invulnOriginal)(frames);
 if(!enabled)return;
 for(int n=0;n<2;n++){
  auto p=Actor(n);
  if(p!=owner&&Eligible(p)&&At<int>((uintptr_t)p+0xc404)<frames)Invulnerable(p,frames);
 }
}
static int __cdecl BombStartHook(){
 auto bomb=At<unsigned char*>(0x4b43c4);
 bool starting=enabled&&bomb&&At<int>((uintptr_t)bomb+0x3c)==0;
 int previousOwner=bombActor;
 uint32_t previousShot=coop_characters::BombShot();
 if(starting)bombActor=Index(Player());
 if(starting)coop_characters::LatchBomb(Player());
 coop_characters::Scope character(Player());
 int result=((int(__cdecl*)())bombStartOriginal)();
 if(!starting||result!=0){bombActor=previousOwner;coop_characters::RestoreBomb(previousShot);return result;}
 // The native initiator finishes its own deathbomb branch after this returns.
 // P1 may already have advanced timer7 to8 before P2 starts its bomb. Use the
 // shared frame's starting eligibility, without undoing any committed miss.
 int frames=At<int>((uintptr_t)Player()+0xc404);if(frames<40)frames=40;
 for(int n=0;n<2;n++){
  auto p=Actor(n);
  if(State(p)==4&&(At<int>((uintptr_t)p+0xa34)<8||(frameOpen&&graceAtFrameStart[n]))){SetStateTimer(p,60);At<int>((uintptr_t)p+0xa28)=1;}
  if((p==Player()||Eligible(p))&&At<int>((uintptr_t)p+0xc404)<frames)Invulnerable(p,frames);
 }
 return result;
}
static void __cdecl CancelBomb(){
 // GUI stage-transition cancellation is outside the normal Bomb tick. Reimu B
 // emits its final explosion sources here, and those still belong to its owner.
 auto old=Player();
 coop_characters::Scope character(coop_characters::BombShot());
 if(enabled&&bombActor>=0)Player()=Actor(bombActor);
 ((void(__cdecl*)())bombCancelOriginal)();
 Player()=old;
}
__declspec(naked) static void BombCancelHook(){__asm {
 push ecx
 call CancelBomb
 pop ecx
 ret
 }}
__declspec(naked) static void BombDamageHook(){__asm {
 cmp suppressBomb,0
 jne skip
 jmp bombDamageOriginal
 skip:
 xor eax,eax
 ret 4
 }}
// Each original invocation consumes only that actor's projectiles/damage
// sources and awards native hit score once. Global direct bomb damage belongs
// to its initiating actor's pass; it must never be evaluated twice.
static int Damage(void* position,void* boxSize,void* original){
 auto fn=(int(__stdcall*)(void*,void*))original;
 if(!enabled)return fn(position,boxSize);
 auto old=Player();bool oldSuppress=suppressBomb;int total=0;
 int owner=bombActor>=0?bombActor:0;
 for(int n=0;n<2;n++){
  auto p=Actor(n);Player()=p;suppressBomb=n!=owner;
  coop_characters::Scope character(p);
  coop_damage_sources::bombDamage=0;coop_damage_sources::capturing=true;
  int amount=fn(position,boxSize);
  coop_damage_sources::capturing=false;
  // Keep the native bomb-only baseline under the shared per-query cap.
  int bombPart=(int)coop_damage_sources::bombDamage;
  if(bombPart>amount)bombPart=amount;
  coop_damage_sources::rawBomb[n]=(uint32_t)bombPart;
  coop_damage_sources::rawShot[n]=(uint32_t)(amount-bombPart);
  auto limit=[&](int value){
   if(State(p)==0||State(p)==2)value/=5;
   auto spell=At<uintptr_t>(0x4b43cc),nativeBomb=At<uintptr_t>(0x4b43c4);
   if(spell&&(At<uint32_t>(spell+0x7c)&1)){
    int id=At<int>(spell+0x78);uint32_t flags=At<uint32_t>((uintptr_t)p+0xc414);
    bool protectedActor=At<int>((uintptr_t)p+0xc404)!=0||(flags&2)!=0;
    if(id>=93&&id<=99&&!(flags&4)&&((nativeBomb&&At<int>(nativeBomb+0x3c)!=0)||protectedActor))value/=5;
   }
   return value;
  };
  amount=limit(amount);bombPart=limit(bombPart);
  coop_damage_sources::nativeBomb[n]=(uint32_t)bombPart;
  coop_damage_sources::shot[n]=(uint32_t)(amount-bombPart);
  total+=amount;
 }
 suppressBomb=oldSuppress;Player()=old;return total;
}
__declspec(naked) static void DamageScaleHook(){__asm {
 cmp enabled,0
 jne skip
 mov eax,dword ptr [ecx+0xa28]
 jmp damageScaleResume
 skip:
 jmp damageScaleSkip
 }}
__declspec(naked) static void SpellScaleHook(){__asm {
 cmp enabled,0
 jne skip
 mov eax,dword ptr [eax+0x78]
 cmp eax,0x5d
 jmp spellScaleResume
 skip:
 jmp spellScaleSkip
 }}
// The mod's future-affecting state contains actor indices, never addresses.
static uint64_t OwnStateHash(){
 return UINT64_C(0x5245530100000000)|(uint64_t)enabled|((uint64_t)credit[0]<<1)|
  ((uint64_t)credit[1]<<2)|((uint64_t)gameOverIssued<<3)|((uint64_t)(bombActor+1)<<4);
}
static void Register(coop_patch::Registry& r){
 respawnAddress=coop_game::Code(0x436ea8);waitAddress=coop_game::Code(0x436f70);gameOverAddress=coop_game::Code(0x436e88);
 rankResume=coop_game::Code(0x4374f4);damageScaleResume=coop_game::Code(0x413d7e);damageScaleSkip=coop_game::Code(0x413d99);
 spellScaleResume=coop_game::Code(0x413dbd);spellScaleSkip=coop_game::Code(0x413df9);
 const unsigned char death[]={0x51,0x53,0xbb,0x01,0,0,0};
 const unsigned char gate[]={0x33,0xdb,0x39,0x1d,0x98,0x0c,0x4b,0,0x7d,0x20};
 const unsigned char rank[]={0xa1,0xcc,0x0c,0x4b,0,0x40};
 const unsigned char start[]={0x57,0x8b,0x3d,0xc4,0x43,0x4b,0};
 const unsigned char invuln[]={0xa1,0x14,0x45,0x4b,0};
 const unsigned char bombDamage[]={0x51,0x8b,0x0d,0xc4,0x43,0x4b,0};
 const unsigned char bombCancel[]={0x51,0x8b,0x0d,0xc4,0x43,0x4b,0};
 const unsigned char scaling[]={0x8b,0x81,0x28,0x0a,0,0};
 const unsigned char spellScaling[]={0x8b,0x40,0x78,0x83,0xf8,0x5d};
 r.Hook100a(0x4381c0,death,sizeof(death),(void*)DeathHook,&deathOriginal);
 r.Hook100a(0x436e7e,gate,sizeof(gate),(void*)RespawnHook);
 r.Hook100a(0x4374ee,rank,sizeof(rank),(void*)RankHook);
 r.Hook100a(0x406bd0,start,sizeof(start),(void*)BombStartHook,&bombStartOriginal);
 r.Hook100a(0x406f40,invuln,sizeof(invuln),(void*)InvulnerabilityHook,&invulnOriginal);
 r.Hook100a(0x406dc0,bombDamage,sizeof(bombDamage),(void*)BombDamageHook,&bombDamageOriginal);
 r.Hook100a(0x406eb0,bombCancel,sizeof(bombCancel),(void*)BombCancelHook,&bombCancelOriginal);
 r.Hook100a(0x413d78,scaling,sizeof(scaling),(void*)DamageScaleHook);
 r.Hook100a(0x413db7,spellScaling,sizeof(spellScaling),(void*)SpellScaleHook);
}
}
