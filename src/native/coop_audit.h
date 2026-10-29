#pragma once
#ifdef COOP_SMOKE_TEST
#include "coop_audit_resources.h"
#if __has_include("coop_audit_targeting.h")
#include "coop_audit_targeting.h"
#define COOP_AUDIT_TARGETING
#endif
#if __has_include("coop_audit_items.h")
#include "coop_audit_items.h"
#define COOP_AUDIT_ITEMS
#endif
#endif

// Fixed, local, destructive fixtures exist only in the separate smoke DLL.
// Production builds expose neither a command mapping nor gameplay mutation API.
namespace coop_audit {
#ifdef COOP_SMOKE_TEST
struct Shared {
 uint32_t magic,version;
 volatile LONG sequence,completed;
 uint32_t family,caseId,result,reserved;
 uint32_t details[32],padding[24];
};
static_assert(sizeof(Shared)==256,"audit ABI");
static Shared* state;
static void Initialize(){
 char name[64];sprintf_s(name,"Local\\TH12CoopAudit_%lu",GetCurrentProcessId());
 auto handle=CreateFileMappingA(INVALID_HANDLE_VALUE,nullptr,PAGE_READWRITE,0,sizeof(Shared),name);
 if(!handle)return;
 state=(Shared*)MapViewOfFile(handle,FILE_MAP_ALL_ACCESS,0,0,sizeof(Shared));
 if(!state){CloseHandle(handle);return;}
 state->magic=0x41423154;state->version=1;
}
static bool Poll(){
 if(!state||!first||!second)return false;
 const LONG sequence=InterlockedCompareExchange(&state->sequence,0,0);
 if(!sequence)return false;
 if(state->completed==sequence)return state->family!=4||!coop_campaign::Active();
 const uint32_t family=state->family,caseId=state->caseId;
 uint32_t result=0x20000000u;
 if(family==1)result=RunResourceAudit(caseId,state->details);
 if(family==4)result=coop_campaign::Configure(caseId,state->details);
#ifdef COOP_AUDIT_TARGETING
 if(family==2)result=RunTargetingAudit(caseId,state->details);
#endif
#ifdef COOP_AUDIT_ITEMS
 if(family==3)result=RunItemAudit(caseId,state->details);
#endif
 state->result=result;MemoryBarrier();InterlockedExchange(&state->completed,sequence);
 return family!=4||result!=0;
}
#else
static void Initialize(){}
static bool Poll(){return false;}
#endif
}
