#pragma once
#include "coop_resources.h"
#include "lockstep.h"

namespace coop_visuals {
static void* drawOriginal;
// Observation only. Local presentation is excluded from gameplay fingerprints.
struct Observation {uint32_t magic,version,localPlayer,reserved,drawn[2],hidden[2],bodyColor[2];};
static Observation* observation;
static HANDLE mapping;
static void Initialize(){
 char name[64];sprintf_s(name,"Local\\TH12CoopVisuals_%lu",GetCurrentProcessId());
 mapping=CreateFileMappingA(INVALID_HANDLE_VALUE,nullptr,PAGE_READWRITE,0,sizeof(Observation),name);
 if(mapping)observation=(Observation*)MapViewOfFile(mapping,FILE_MAP_WRITE,0,0,sizeof(Observation));
 if(observation){observation->magic=0x56323154;observation->version=1;observation->localPlayer=coop_lockstep::LocalPlayer();}
}
static int __cdecl Draw(void* vm,void* manager){
 using namespace coop_resources;
 auto address=(uintptr_t)vm;
 int body=-1;bool remote=false;
 if(enabled&&vm)for(int n=0;n<2;n++){
  auto actor=(uintptr_t)Actor(n);
  const bool partner=coop_lockstep::Active()&&coop_lockstep::LocalPlayer()!=(uint32_t)(n+1);
  const auto marker=At<uint32_t>(actor+0x8258);
  if(marker&&At<uint32_t>(address)==marker){
   if(observation)++(partner?observation->hidden[n]:observation->drawn[n]);
   if(partner)return -1; // Same return as a natively invisible VM; no animation mutation.
  }
  if(address==actor+0x14){body=n;remote=partner;}
 }
 uint32_t color=0;
 if(body>=0){
  color=At<uint32_t>(address+0x3bc);
  if(remote)At<uint32_t>(address+0x3bc)=(color&0xffffff)|(((color>>24)*112/255)<<24);
  if(observation)observation->bodyColor[body]=At<uint32_t>(address+0x3bc);
 }
 int result;
 __asm {
  push manager
  mov eax,vm
  call drawOriginal
  mov result,eax
 }
 if(body>=0)At<uint32_t>(address+0x3bc)=color;
 return result;
}
__declspec(naked) static void DrawHook(){__asm {
 push dword ptr [esp+4]
 push eax
 call Draw
 add esp,8
 ret 4
}}
static void Register(coop_patch::Registry& registry){
 const unsigned char draw[]={0x55,0x8b,0xec,0x83,0xe4,0xf8};
 registry.Hook100a(0x45c8e0,draw,sizeof(draw),(void*)DrawHook,&drawOriginal);
}
}
