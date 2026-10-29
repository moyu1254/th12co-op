#pragma once
#include <windows.h>
#include <stdint.h>
#include "lockstep.h"

// Startup metadata is separate from network input. Offline games use the same
// suspended entry-point installation fence without enabling a lockstep barrier.
namespace coop_boot {
static uint32_t primaryThreadId;
static uint32_t PrimaryThread(){return primaryThreadId;}
static bool OwnThread(uint32_t id){
 if(!id)return false;
 HANDLE thread=OpenThread(THREAD_QUERY_LIMITED_INFORMATION,FALSE,id);
 if(!thread)return false;
 const bool same=GetProcessIdOfThread(thread)==GetCurrentProcessId();
 CloseHandle(thread);return same;
}
static bool Initialize(){
 primaryThreadId=0;
 char name[64];sprintf_s(name,"Local\\TH12CoopBoot_%lu",GetCurrentProcessId());
 HANDLE mapping=OpenFileMappingA(FILE_MAP_READ,FALSE,name);
 if(!mapping){
  // Existing network diagnostics already supply the verified primary thread
  // and use injector --early. Keep that explicit early-launch contract.
  if(coop_lockstep::Active()&&OwnThread(coop_lockstep::PrimaryThread())){
   primaryThreadId=coop_lockstep::PrimaryThread();return true;
  }
  return false;
 }
 auto header=(const uint32_t*)MapViewOfFile(mapping,FILE_MAP_READ,0,0,16);
 bool valid=false;
 if(header){
  const uint32_t magic=header[0],version=header[1],tid=header[2],early=header[3];
  valid=magic==0x42323154&&version==1&&early==1&&OwnThread(tid);
  if(valid&&coop_lockstep::Active()&&coop_lockstep::PrimaryThread()!=tid)valid=false;
  if(valid)primaryThreadId=tid;
  UnmapViewOfFile(header);
 }
 CloseHandle(mapping);return valid;
}
}
