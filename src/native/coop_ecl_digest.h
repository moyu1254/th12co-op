#pragma once
#include <windows.h>
#include <stdint.h>
#include <stdlib.h>

// Read-only, partial ECL fingerprint. Never dereference an instruction pointer
// or hash stack bytes, pointers, allocation padding or uninitialized async flags.
namespace coop_ecl_digest {
// Defensive work budgets, not claims about the engine's supported maxima.
constexpr uint32_t MaxManagers=32,MaxSubs=8192,MaxTotalSubs=32768;
constexpr uint32_t MaxContexts=256,MaxTotalContexts=65536,MaxPcDelta=1024*1024;
struct Entry {uintptr_t start,end;uint32_t row;};
struct Manager {uintptr_t address;uint32_t first,count;};
struct Stats {uint32_t managers,subs,contexts,asyncContexts,maxDelta,error;};
static Entry entries[MaxTotalSubs];
static Manager managers[MaxManagers];
static Stats stats;
template<class T> static T Read(uintptr_t p){return *reinterpret_cast<const T*>(p);}
static bool Reject(uint32_t error){stats.error=error;return false;}
static void BeginFrame(){stats={};}
static bool Readable(uintptr_t p,size_t bytes){
 if(!p||!bytes||bytes>UINTPTR_MAX-p)return false;
 const uintptr_t end=p+bytes;
 while(p<end){
  MEMORY_BASIC_INFORMATION region={};
  constexpr DWORD readable=PAGE_READONLY|PAGE_READWRITE|PAGE_WRITECOPY|
   PAGE_EXECUTE_READ|PAGE_EXECUTE_READWRITE|PAGE_EXECUTE_WRITECOPY;
  if(!VirtualQuery((void*)p,&region,sizeof(region))||region.State!=MEM_COMMIT||
     (region.Protect&(PAGE_GUARD|PAGE_NOACCESS))||!(region.Protect&readable))return false;
  uintptr_t next=(uintptr_t)region.BaseAddress+region.RegionSize;
  if(next<=p)return false;p=next;
 }
 return true;
}
static int __cdecl Compare(const void* a,const void* b){
 auto x=((const Entry*)a)->start,y=((const Entry*)b)->start;
 return x<y?-1:x>y?1:0;
}
static const Manager* Prepare(uintptr_t address){
 for(uint32_t n=0;n<stats.managers;n++)if(managers[n].address==address)return &managers[n];
 if(stats.managers==MaxManagers||!Readable(address,0x90)){Reject(1);return nullptr;}
 const uint32_t files=Read<uint32_t>(address+4),count=Read<uint32_t>(address+8);
 const uintptr_t table=Read<uintptr_t>(address+0x8c);
 if(!files||files>32||count>MaxSubs||count>MaxTotalSubs-stats.subs||
    (count&&!Readable(table,(size_t)count*8))){Reject(2);return nullptr;}
 const uint32_t firstEntry=stats.subs;
 for(uint32_t n=0;n<count;n++){
  const uintptr_t header=Read<uintptr_t>(table+n*8+4);
  if(!header||header>UINTPTR_MAX-0x10-MaxPcDelta){Reject(3);return nullptr;}
  entries[firstEntry+n]={header+0x10,header+0x10+MaxPcDelta,n};
 }
 qsort(entries+firstEntry,count,sizeof(Entry),Compare);
 for(uint32_t n=0;n+1<count;n++){
  auto& current=entries[firstEntry+n];const auto& next=entries[firstEntry+n+1];
  if(next.start-current.start<=0x10){Reject(3);return nullptr;}
  // Exclude the next subroutine's 16-byte header. The final subroutine has
  // only the explicit numeric delta cap: FileManager stores no file lengths.
  if(next.start-0x10<current.end)current.end=next.start-0x10;
 }
 managers[stats.managers]={address,firstEntry,count};stats.subs+=count;
 return &managers[stats.managers++];
}
template<class H> static bool Pc(H& hash,const Manager& manager,uintptr_t pc){
 if(!pc){hash.word(0);return true;}
 uint32_t lo=0,hi=manager.count;
 while(lo<hi){uint32_t mid=lo+(hi-lo)/2;if(entries[manager.first+mid].start<=pc)lo=mid+1;else hi=mid;}
 if(!lo)return Reject(4);
 const auto& entry=entries[manager.first+lo-1];
 if(pc>=entry.end)return Reject(4);
 uint32_t delta=(uint32_t)(pc-entry.start);
 hash.word(1);hash.word(entry.row);hash.word(delta);
 if(delta>stats.maxDelta)stats.maxDelta=delta;
 return true;
}
template<class H> static bool Append(H& hash,uintptr_t full){
 if(!Readable(full,0x103c))return Reject(5);
 const auto* manager=Prepare(Read<uintptr_t>(full+0x102c));if(!manager)return false;
 // Native reset 0x411E20 initializes the embedded head to the primary context.
 // 0x4690F0 initializes async time/PC/SP/base/id/enemy/difficulty only.
 uintptr_t node=full+0x1030,previousNode=0;uint32_t count=0;
 hash.word(manager->count);
 while(node){
  if(count==MaxContexts||stats.contexts==MaxTotalContexts)return Reject(6);
  if(!Readable(node,12)||Read<uintptr_t>(node+8)!=previousNode)return Reject(7);
  const uintptr_t context=Read<uintptr_t>(node);
  if((!count&&context!=full+8)||!Readable(context,0x1024)||Read<uintptr_t>(context+0x1014)!=full)return Reject(8);
  const uint32_t stack=Read<uint32_t>(context+0x1008),base=Read<uint32_t>(context+0x100c);
  if(stack>0x1000||base>stack||((stack|base)&3))return Reject(9);
  hash.word(count);hash.word(Read<uint32_t>(context));
  if(!Pc(hash,*manager,Read<uintptr_t>(context+4)))return false;
  hash.word(stack);hash.word(base);hash.word(Read<uint32_t>(context+0x1010));
  hash.word(Read<uint8_t>(context+0x101c));
  if(!count){
   hash.word(Read<uint32_t>(context+0x1018));
   hash.word(Read<uint32_t>(context+0x1020)&1u);
  }
  ++stats.contexts;if(count)++stats.asyncContexts;++count;
  previousNode=node;node=Read<uintptr_t>(node+4);
 }
 hash.word(count);return true;
}
}
