#pragma once
#include <windows.h>
#include <stdint.h>
#include <string.h>
#include <vector>
#include "game_layout.h"

// Register every code change before touching the game. A failed signature or
// allocation leaves the game unmodified; a protection failure rolls back writes.
namespace coop_patch {
class Registry {
 struct Entry {
  uintptr_t address;
  std::vector<unsigned char> expected, replacement;
  void** original;
  unsigned char* trampoline;
 };
 std::vector<Entry> entries;
 uintptr_t failedAddress=0;
 bool valid=true;
 static void Jump(unsigned char* code,uintptr_t from,uintptr_t to) {
  code[0]=0xe9;
  const uint32_t displacement=(uint32_t)(to-from-5);
  memcpy(code+1,&displacement,4);
 }
public:
 void Hook100a(uintptr_t address,const unsigned char* expected,size_t count,void* target,void** original=nullptr) {
  if(count<5||!target){valid=false;failedAddress=coop_game::Code(address);return;}
  Entry e{coop_game::Code(address),{expected,expected+count},std::vector<unsigned char>(count,0x90),original,nullptr};
  if(count>=5)Jump(e.replacement.data(),e.address,(uintptr_t)target);
  entries.push_back(e);
 }
 void Bytes100a(uintptr_t address,const unsigned char* expected,const unsigned char* replacement,size_t count) {
  entries.push_back({coop_game::Code(address),{expected,expected+count},{replacement,replacement+count},nullptr,nullptr});
 }
 uintptr_t FailedAddress()const{return failedAddress;}
 size_t Count()const{return entries.size();}
 bool Validate() {
  if(!valid)return false;
  for(size_t i=0;i<entries.size();++i){
   const auto& e=entries[i];
   if(e.expected.empty()||memcmp((void*)e.address,e.expected.data(),e.expected.size())){failedAddress=e.address;return false;}
   for(size_t j=0;j<i;++j){
    const auto& other=entries[j];
    if(e.address<other.address+other.expected.size()&&other.address<e.address+e.expected.size()){
     failedAddress=e.address;return false;
    }
   }
  }
  return true;
 }
 bool Install() {
  if(!Validate())return false;
  for(auto& e:entries)if(e.original){
   if(e.expected.size()<5){failedAddress=e.address;Release();return false;}
   e.trampoline=(unsigned char*)VirtualAlloc(nullptr,e.expected.size()+5,MEM_COMMIT|MEM_RESERVE,PAGE_READWRITE);
   if(!e.trampoline){failedAddress=e.address;Release();return false;}
   memcpy(e.trampoline,e.expected.data(),e.expected.size());
   Jump(e.trampoline+e.expected.size(),(uintptr_t)e.trampoline+e.expected.size(),e.address+e.expected.size());
   DWORD old;
   if(!VirtualProtect(e.trampoline,e.expected.size()+5,PAGE_EXECUTE_READ,&old)){failedAddress=e.address;Release();return false;}
   FlushInstructionCache(GetCurrentProcess(),e.trampoline,e.expected.size()+5);
  }
  // Publish trampolines before a newly installed entry can use one.
  for(auto& e:entries)if(e.original)*e.original=e.trampoline;
  size_t applied=0;
  for(auto& e:entries){
   DWORD old;
   if(!VirtualProtect((void*)e.address,e.replacement.size(),PAGE_EXECUTE_READWRITE,&old)){
    failedAddress=e.address;Rollback(applied);return false;
   }
   memcpy((void*)e.address,e.replacement.data(),e.replacement.size());
   FlushInstructionCache(GetCurrentProcess(),(void*)e.address,e.replacement.size());
   ++applied;
   DWORD ignored;
   if(!VirtualProtect((void*)e.address,e.replacement.size(),old,&ignored)){
    failedAddress=e.address;Rollback(applied);return false;
   }
  }
  return true;
 }
private:
 void Release(){
  for(auto& e:entries)if(e.trampoline){VirtualFree(e.trampoline,0,MEM_RELEASE);e.trampoline=nullptr;}
 }
 void Rollback(size_t count){
  while(count){
   auto& e=entries[--count];DWORD old;
   if(VirtualProtect((void*)e.address,e.expected.size(),PAGE_EXECUTE_READWRITE,&old)){
    memcpy((void*)e.address,e.expected.data(),e.expected.size());
    FlushInstructionCache(GetCurrentProcess(),(void*)e.address,e.expected.size());
    DWORD ignored;VirtualProtect((void*)e.address,e.expected.size(),old,&ignored);
   }
  }
  // Do not free trampolines after a partial installation: an already-entered
  // hook may still use one until the owning launcher closes this process.
 }
};
}
