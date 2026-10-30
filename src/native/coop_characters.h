#pragma once
#include "patch.h"
#include "lockstep.h"

// Native SHT and ANM allocations belong to an actor. Only the original
// character/weapon dispatch globals need a scoped view of that actor.
namespace coop_characters {
static bool configured;
static bool menuSelection=true;
static uint32_t menuPlayer;
static uintptr_t weaponContinue,practiceContinue,weaponBack;
static uint32_t selected[2], active[2], bombShot=UINT32_MAX;
static unsigned char* actors[2];
static uint32_t constructionSlot=7;
static void *stageOriginal,*bombDestroyOriginal;
static uintptr_t assetContinue,bombEndDispatch;
struct ActorSnapshot {uint32_t present,shot,anmSlot,powerLevels,powerUnit;int32_t speeds[4];float radii[3],x,y;uint32_t state,focus;};
struct Snapshot {uint32_t magic,version,sequence,bomb;ActorSnapshot actors[2];};
static_assert(sizeof(Snapshot)==144,"actor telemetry ABI");
static Snapshot* snapshot;
static HANDLE snapshotMapping;
template<class T> static T& At(uintptr_t p){return *reinterpret_cast<T*>(p);}
static uint32_t NativeShot(){return At<uint32_t>(0x4b0c90)*2+At<uint32_t>(0x4b0c94);}
static void Select(uint32_t shot){At<uint32_t>(0x4b0c90)=shot/2;At<uint32_t>(0x4b0c94)=shot%2;}
struct Scope {
 uint32_t character,weapon;
 explicit Scope(uint32_t shot):character(At<uint32_t>(0x4b0c90)),weapon(At<uint32_t>(0x4b0c94)){
  if(shot<6)Select(shot);
 }
 explicit Scope(unsigned char* actor):Scope(actor&&actor==actors[0]?active[0]:actor&&actor==actors[1]?active[1]:UINT32_MAX){}
 ~Scope(){At<uint32_t>(0x4b0c90)=character;At<uint32_t>(0x4b0c94)=weapon;}
};
static bool Initialize(){
 char telemetryName[64];sprintf_s(telemetryName,"Local\\TH12CoopActors_%lu",GetCurrentProcessId());
 snapshotMapping=CreateFileMappingA(INVALID_HANDLE_VALUE,nullptr,PAGE_READWRITE,0,sizeof(Snapshot),telemetryName);
 if(snapshotMapping)snapshot=(Snapshot*)MapViewOfFile(snapshotMapping,FILE_MAP_WRITE,0,0,sizeof(Snapshot));
 char name[64];sprintf_s(name,"Local\\TH12CoopPlayers_%lu",GetCurrentProcessId());
 HANDLE mapping=OpenFileMappingA(FILE_MAP_READ,FALSE,name);
 if(!mapping)return GetLastError()==ERROR_FILE_NOT_FOUND;
 auto header=(const uint32_t*)MapViewOfFile(mapping,FILE_MAP_READ,0,0,16);
 const bool valid=header&&header[0]==0x43323154&&header[1]==1&&header[2]<6&&header[3]<6;
 if(valid){selected[0]=header[2];selected[1]=header[3];configured=true;menuSelection=false;}
 if(header)UnmapViewOfFile(header);CloseHandle(mapping);return valid;
}
static bool SinglePlayer(){
 auto gm=At<uintptr_t>(0x4b44e8);
 return (At<uint32_t>(0x4b0ce0)&0x20)||(gm&&At<int>(gm+0x74));
}
static uint32_t MenuInput(uint32_t primary,uint32_t partner){
 auto menu=At<uintptr_t>(0x4b4530);
 return menuSelection&&menuPlayer==1&&menu&&(At<uint32_t>(menu+0x1c)==6||At<uint32_t>(menu+0x1c)==7)?partner:primary;
}
// Reuse the native character/equipment screens and their cancel animation.
// The first confirmation returns to character selection for the partner;
// only the second confirmation can start a stage (including Practice/Extra).
static int __cdecl ConfirmWeapon(uintptr_t menu){
 if(!menuSelection||SinglePlayer())return 0;
 selected[menuPlayer]=At<uint32_t>(0x4b0c90)*2+At<uint32_t>(menu+0x28);
 if(menuPlayer==0){menuPlayer=1;configured=false;return 1;}
 configured=true;return 0;
}
__declspec(naked) static void WeaponConfirm(){__asm {
 pushfd
 pushad
 push ebp
 call ConfirmWeapon
 add esp,4
 test eax,eax
 jnz partner
 popad
 popfd
 mov ecx,dword ptr [ebp+28h]
 lea eax,[ebp+28h]
 jmp weaponContinue
partner:
 popad
 popfd
 jmp weaponBack
}}
__declspec(naked) static void PracticeConfirm(){__asm {
 pushfd
 pushad
 push ebp
 call ConfirmWeapon
 add esp,4
 test eax,eax
 jnz partner
 popad
 popfd
 mov ecx,dword ptr [ebp+28h]
 lea eax,[ebp+28h]
 jmp practiceContinue
partner:
 popad
 popfd
 jmp weaponBack
}}
__declspec(noinline) static void MenuFinished(uintptr_t menu){
 if(!menuSelection)return;
 auto state=At<uint32_t>(menu+0x1c);
 if(state!=6&&state!=7){
  if(configured)Select(selected[0]);
  menuPlayer=0;
 }
 if(state!=6&&state!=7)return;
 auto manager=At<uintptr_t>(0x4b43b8);if(!manager)return;
 const char* label=menuPlayer?"2P":"1P";float position[3]={304.f,24.f,0.f};
 // This function precedes the 1.00b insertion and has identical bytes/address.
 const uintptr_t create=0x4014f0;
 // AsciiManager::create_string (EAX text, EBX position, ECX manager).
 __asm {push ebx
 lea ebx,position
 mov eax,label
 mov ecx,manager
 call create
 pop ebx}
}
static void __cdecl BeginStage(){
 // Run before the stage worker chooses story, GUI, score category or SHT.
 // The host's P1 is the single shared story route on both peers.
 if(configured&&!SinglePlayer())Select(selected[0]);
}
__declspec(naked) static void StageHook(){__asm {
 pushfd
 pushad
 call BeginStage
 popad
 popfd
 jmp stageOriginal
}}
static void BeginActors(){
 active[0]=configured?selected[0]:NativeShot();
 active[1]=configured?selected[1]:NativeShot();
 actors[0]=actors[1]=nullptr;
}
static void Bind(unsigned char* p1,unsigned char* p2){actors[0]=p1;actors[1]=p2;}
static uint32_t Shot(unsigned char* actor){return actor&&actor==actors[1]?active[1]:active[0];}
static void LatchBomb(unsigned char* actor){bombShot=Shot(actor);}
static uint32_t BombShot(){return bombShot;}
static void RestoreBomb(uint32_t shot){bombShot=shot;}
static void Append(coop_lockstep::Hash& h){
 h.word(menuSelection);h.word(menuPlayer);
 h.word(configured?1:0);h.word(configured?selected[0]:UINT32_MAX);h.word(configured?selected[1]:UINT32_MAX);
 h.word(actors[0]?active[0]:UINT32_MAX);h.word(actors[1]?active[1]:UINT32_MAX);h.word(bombShot);
}
// Observation only: no external value from this mapping is read by the game.
// P2 has no native singleton or registered lifecycle node after pairing, so
// publish scalar evidence without exposing or accepting process pointers.
static void Publish(){
 if(!snapshot)return;
 Snapshot next{};next.magic=0x41323154;next.version=1;next.bomb=bombShot;
 for(int n=0;n<2;n++){
  auto actor=(uintptr_t)actors[n];auto& out=next.actors[n];if(!actor)continue;
  out.present=1;out.shot=active[n];auto asset=At<uintptr_t>(actor+0x10),sht=At<uintptr_t>(actor+0xa2c);
  out.anmSlot=asset?At<uint32_t>(asset):UINT32_MAX;
  if(sht){out.powerLevels=At<uint32_t>(sht+0x20);out.powerUnit=At<uint32_t>(sht+0x24);memcpy(out.radii,(void*)(sht+4),sizeof(out.radii));}
  memcpy(out.speeds,(void*)(actor+0x990),sizeof(out.speeds));
  out.x=At<float>(actor+0x97c);out.y=At<float>(actor+0x980);out.state=At<uint32_t>(actor+0xa28);out.focus=At<uint32_t>(actor+0xc598);
 }
 static uint32_t sequence;next.sequence=++sequence;memcpy(snapshot,&next,sizeof(next));
}
// Slot 26 has no allocation in the verified loader callers. The original ANM
// loader retains the slot number in every sprite/VM, so swapping slot 7's
// pointer at draw time would not isolate asynchronous ANM callbacks.
__declspec(naked) static void AssetSlotHook(){__asm {
 mov ecx,constructionSlot
 jmp assetContinue
}}
static void __stdcall BombDestroy(void* bomb){
 Scope scope(bombShot);
 ((void(__stdcall*)(void*))bombDestroyOriginal)(bomb);
 bombShot=UINT32_MAX;
}
__declspec(noinline) static int EndBomb(void* bomb){
 if(!At<int>((uintptr_t)bomb+0x3c))return 0;
 Scope scope(bombShot);int result;
 __asm {mov eax,bomb
 call bombEndDispatch
 mov result,eax}
 return result;
}
__declspec(naked) static void BombEnd(){__asm {
 push eax
 call EndBomb
 add esp,4
 ret
}}
static void Register(coop_patch::Registry& registry){
 weaponContinue=coop_game::Code(0x445e81);weaponBack=coop_game::Code(0x445f31);
 practiceContinue=coop_game::Code(0x445e47);
 const unsigned char confirm[]={0x8b,0x4d,0x28,0x8d,0x45,0x28};
 registry.Hook100a(0x445e7b,confirm,sizeof(confirm),(void*)WeaponConfirm);
 registry.Hook100a(0x445e41,confirm,sizeof(confirm),(void*)PracticeConfirm);
 assetContinue=coop_game::Code(0x435ad7);
 bombEndDispatch=coop_game::Code(0x406e46);
 const unsigned char slot[]={0xb9,7,0,0,0};
 const unsigned char stage[]={0x55,0x8b,0xec,0x83,0xe4,0xf8};
 unsigned char destroy[]={0x6a,0xff,0x68,0xbb,0x75,0x49,0};
 const auto handler=(uint32_t)coop_game::Code(0x4975bb);memcpy(destroy+3,&handler,4);
 const unsigned char end[]={0x83,0x78,0x3c,0,0x74,0x4e};
 registry.Hook100a(0x435ad2,slot,sizeof(slot),(void*)AssetSlotHook);
 registry.Hook100a(0x421cb0,stage,sizeof(stage),(void*)StageHook,&stageOriginal);
 registry.Hook100a(0x406910,destroy,sizeof(destroy),(void*)BombDestroy,&bombDestroyOriginal);
 // Reproduce the short early return in EndBomb; do not relocate its branch.
 registry.Hook100a(0x406e40,end,sizeof(end),(void*)BombEnd);
}
}
