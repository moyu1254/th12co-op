#pragma once
#include <stdio.h>
#include "patch.h"

// Retail1.00a omits formatting scores >=1,000,000,000 and queues an
// uninitialized stack string. Match the official1.00b four-group display,
// retaining native arithmetic and every pre-billion (including signed) path.
namespace coop_scorecompat {
static void* original;
__declspec(noinline) static void QueueText(const char* text,const float* position){
 const uintptr_t target=0x4014f0; // Before the version-dependent code insertion.
 __asm {
  mov eax,text
  mov ebx,position
  mov ecx,dword ptr ds:[0x4b43b8]
  call target
 }
}
static void __cdecl FormatBillion(int score,const float* position){
 char text[32];
 _snprintf_s(text,sizeof(text),_TRUNCATE,"%d,%.3d,%.3d,%.3d",
  score/1000000000,(score/1000000)%1000,(score/1000)%1000,score%1000);
 QueueText(text,position);
}
__declspec(naked) static void Hook(){__asm {
 cmp ecx,1000000000
 jl nativeFormat
 push ebx
 push ecx
 call FormatBillion
 add esp,8
 ret
 nativeFormat:
 jmp original
}}
static void Register(coop_patch::Registry& registry){
 // Do not apply Code(+0x20): this formatter itself is BEFORE that insertion.
 // Only1.00a needs a patch; the two1.00b profiles retain their official fix.
 if(coop_game::codeShift!=0)return;
 const unsigned char expected[]={0x81,0xec,0x04,0x01,0,0};
 registry.Hook100a(0x401610,expected,sizeof(expected),(void*)Hook,&original);
}
}
