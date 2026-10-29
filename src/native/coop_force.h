#pragma once

namespace coop_force {
static void* original;
static uintptr_t continuation;
__declspec(noinline) static void WritePosition(const float* position) {
    const uintptr_t writer=coop_game::Code(0x412550);
    __asm {
        mov edi,position
        call writer
    }
}
static void MoveOne(unsigned char* actor,unsigned char* field) {
    if(!actorEligible(actor))return;
    float position[3]={mem<float>((uintptr_t)actor+0x97c),mem<float>((uintptr_t)actor+0x980),mem<float>((uintptr_t)actor+0x984)};
    float direction[3]={mem<float>((uintptr_t)field+0x34)-position[0],mem<float>((uintptr_t)field+0x38)-position[1],0.f};
    auto normalize=(float*(__stdcall*)(float*,const float*))coop_game::Code(0x46c69c);
    normalize(direction,direction);
    const float strength=mem<float>((uintptr_t)field+0x258);
    volatile float scaledX=direction[0]*strength,scaledY=direction[1]*strength;
    position[0]+=scaledX;position[1]+=scaledY;
    auto old=singleton();singleton()=actor;
    WritePosition(position);
    singleton()=old;
}
static void __cdecl Apply(unsigned char* field) {
    MoveOne(first,field);MoveOne(second,field);
}
// Only replace the player-local prefix. The original shared bullet loop runs
// once afterward, and the native mode==1 guard remains before this hook.
__declspec(naked) static void Hook(){__asm {
    pushfd
    cmp second,0
    je nativeSinglePlayer
    popfd
    pushfd
    pushad
    mov ebp,esp
    and esp,0fffffff0h
    sub esp,512
    fxsave [esp]
    fninit
    fldcw word ptr [esp]
    push dword ptr [ebp+8]
    call Apply
    add esp,4
    fxrstor [esp]
    mov esp,ebp
    popad
    popfd
    jmp continuation
nativeSinglePlayer:
    popfd
    jmp original
}}
static void Register(coop_patch::Registry& registry) {
    static const unsigned char expected[]={0xa1,0x14,0x45,0x4b,0};
    continuation=coop_game::Code(0x41aee1);
    registry.Hook100a(0x41ae17,expected,sizeof(expected),(void*)Hook,&original);
}
static void Reset(){}
static uint32_t StateHash(){return 0;}
}
