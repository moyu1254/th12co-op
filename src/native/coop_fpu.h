#pragma once

// New x86 Windows fibers start at CW=027f, even when the game/D3D caller is
// using 007f. Reused fibers also retain their previous invocation's context.
// Transfer the actual native call environment explicitly at each invocation.
struct alignas(16) CoopFpuState {
 unsigned char bytes[512];
 __declspec(noinline) void Save(){auto p=bytes;__asm {mov eax,p
 fxsave [eax]}}
 __declspec(noinline) void Restore()const{auto p=bytes;__asm {mov eax,p
 fxrstor [eax]}}
};
