#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <assert.h>
#include <stdio.h>
#include "coop_fpu.h"

static void* scheduler;
static CoopFpuState initial,finished;
static unsigned short observedCW;
static unsigned observedMXCSR;
static void WINAPI Worker(void*){
 for(;;){
  initial.Restore();
  __asm {fnstcw observedCW
  stmxcsr observedMXCSR}
  unsigned short nativeReturnedCW=0x037f;
  __asm fldcw nativeReturnedCW
  finished.Save();
  SwitchToFiber(scheduler);
 }
}
int main(){
 CoopFpuState original;original.Save();
 scheduler=ConvertThreadToFiberEx(nullptr,FIBER_FLAG_FLOAT_SWITCH);assert(scheduler);
 void* fiber=CreateFiberEx(0,0,FIBER_FLAG_FLOAT_SWITCH,Worker,nullptr);assert(fiber);
 for(int pass=0;pass<2;pass++){
  unsigned short chosen=pass?0x0a7f:0x007f;
  unsigned mxcsr=pass?0x5f80:0x3f80;
  __asm {fldcw chosen
  ldmxcsr mxcsr}
  initial.Save();SwitchToFiber(fiber);
  if(observedCW!=chosen||observedMXCSR!=mxcsr){
   printf("fiber FPU mismatch: CW=%04x/%04x MXCSR=%08x/%08x\n",observedCW,chosen,observedMXCSR,mxcsr);
   DeleteFiber(fiber);ConvertFiberToThread();original.Restore();return 1;
  }
  finished.Restore();
  unsigned short returned;__asm fnstcw returned
  assert(returned==0x037f);
 }
 DeleteFiber(fiber);assert(ConvertFiberToThread());original.Restore();
 puts("fiber FPU: native caller CW/MXCSR on new and reused fibers; native return context: PASS");
 return 0;
}
