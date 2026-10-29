#pragma once

// Keep native player-shot target ownership independent for both real actors.
// Enemy aim selection is handled separately by coop_targeting.
namespace coop_homing {
static void *selectionOriginal,*clearOriginal;
static uintptr_t selectionContinue;

static void ClearOne(unsigned char* actor,void* enemy) {
    if(!actor)return;
    if(mem<void*>((uintptr_t)actor+0x8980)==enemy) {
        mem<void*>((uintptr_t)actor+0x8980)=nullptr;
        mem<unsigned char>((uintptr_t)actor+0x8984)=0;
    }
    for(unsigned i=0;i<256;++i) {
        auto& current=mem<void*>((uintptr_t)actor+0xaac+i*0x78);
        if(current==enemy)current=nullptr;
    }
}
static void __cdecl ClearEnemy(void* enemy) {
    ClearOne(first,enemy);
    if(second!=first)ClearOne(second,enemy);
}

static void SelectOne(unsigned char* actor,unsigned char* enemyData) {
    if(!actorEligible(actor))return;
    auto& current=mem<unsigned char*>((uintptr_t)actor+0x8980);
    auto& locked=mem<unsigned char>((uintptr_t)actor+0x8984);
    if(current) {
        // Match the executable's comparison and lock ordering, including float
        // rounding, instead of guessing a different homing-shot algorithm.
        const float x=mem<float>((uintptr_t)actor+0x97c);
        const float oldDistance=fabsf(mem<float>((uintptr_t)current+0x1074)-x);
        const float newDistance=fabsf(mem<float>((uintptr_t)enemyData+0x34)-x);
        if(!(newDistance>oldDistance))return;
    }
    if(!locked)current=mem<unsigned char*>((uintptr_t)enemyData+0x174c);
    locked=1;
}
static void __cdecl SelectPlayers(unsigned char* enemyData) {
    SelectOne(first,enemyData);
    SelectOne(second,enemyData);
}

__declspec(naked) static void SelectionHook(){__asm {
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
    push ebx
    call SelectPlayers
    add esp,4
    fxrstor [esp]
    mov esp,ebp
    popad
    popfd
    jmp selectionContinue
nativeSinglePlayer:
    popfd
    jmp selectionOriginal
}}

// The original destructor stack argument is at ESP+0x28 at this instruction.
// CALL adds four bytes, and PUSHFD/PUSHAD add another 36 bytes, hence +0x50.
__declspec(naked) static void DestructorPlayerLoad(){__asm {
    pushfd
    pushad
    push dword ptr [esp+50h]
    call ClearEnemy
    add esp,4
    popad
    popfd
    mov eax,dword ptr ds:[4b4514h]
    ret
}}

// A second native release helper takes the dying enemy in EDX. Retain its
// original register/return behavior after clearing both actor collections.
__declspec(naked) static void ClearHook(){__asm {
    pushfd
    pushad
    push edx
    call ClearEnemy
    add esp,4
    popad
    popfd
    jmp clearOriginal
}}

static void Register(coop_patch::Registry& registry) {
    static const unsigned char edi[]={0x8b,0x3d,0x14,0x45,0x4b,0};
    static const unsigned char eax[]={0xa1,0x14,0x45,0x4b,0};
    selectionContinue=coop_game::Code(0x414390);
    registry.Hook100a(0x414329,edi,sizeof(edi),(void*)SelectionHook,&selectionOriginal);
    coop_targeting::RegisterLoad(registry,0x41362b,eax,sizeof(eax),(void*)DestructorPlayerLoad);
    registry.Hook100a(0x43abd0,eax,sizeof(eax),(void*)ClearHook,&clearOriginal);
}
static void Reset(){} // State remains in the native actor/shot objects.
static uint32_t StateHash(){return 0;}
}
