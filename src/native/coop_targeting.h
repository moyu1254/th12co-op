#pragma once

// Included after the native player globals, actorEligible(), and patch registry.
// These hooks change only aiming reads. PLAYER_PTR remains the real actor owner
// for damage, collisions, bombs, lifecycle, items and scripted movement.
namespace coop_targeting {
static unsigned char* target;
static uint32_t depth;
static void *eclOriginal, *volleyOriginal, *angleOriginal;
static uintptr_t spawnDistancePassed;

static unsigned char* Nearest(const float* origin) {
    const bool a=actorEligible(first),b=actorEligible(second);
    if(!a)return b?second:(first?first:mem<unsigned char*>(PLAYER));
    if(!b)return first;
    const double ax=(double)mem<float>((uintptr_t)first+0x97c)-origin[0];
    const double ay=(double)mem<float>((uintptr_t)first+0x980)-origin[1];
    const double bx=(double)mem<float>((uintptr_t)second+0x97c)-origin[0];
    const double by=(double)mem<float>((uintptr_t)second+0x980)-origin[1];
    return bx*bx+by*by<ax*ax+ay*ay?second:first; // P1 wins exact ties.
}

struct Scope {
    unsigned char* oldTarget;
    uint32_t oldDepth;
    Scope(const float* origin,bool inherit):oldTarget(target),oldDepth(depth) {
        if(!inherit||!depth)target=Nearest(origin);
        ++depth;
    }
    explicit Scope(unsigned char* actor):oldTarget(target),oldDepth(depth) {target=actor;++depth;}
    ~Scope(){target=oldTarget;depth=oldDepth;}
};

// Replacing a MOV with CALL must preserve flags and every non-result register.
// These loaders do no floating-point work, so in-progress native x87 expressions
// are unaffected. Outside a scope, the original real-player pointer is returned.
__declspec(naked) static void LoadEax(){__asm {
    pushfd
    mov eax,target
    test eax,eax
    jnz selected
    mov eax,dword ptr ds:[4b4514h]
selected:
    popfd
    ret
}}
__declspec(naked) static void LoadEcx(){__asm {
    pushfd
    mov ecx,target
    test ecx,ecx
    jnz selected
    mov ecx,dword ptr ds:[4b4514h]
selected:
    popfd
    ret
}}
__declspec(naked) static void LoadEdx(){__asm {
    pushfd
    mov edx,target
    test edx,edx
    jnz selected
    mov edx,dword ptr ds:[4b4514h]
selected:
    popfd
    ret
}}

static unsigned char* __cdecl SpawnNearest(const float* origin) {
    if(!actorEligible(first)&&!actorEligible(second))return nullptr;
    return Nearest(origin);
}
// The native spawn-distance expression already has live x87 values. Preserve
// the complete FPU/SSE image, flags and registers around target selection. The
// chosen nearest live player makes the native distance rejection an any-player
// test. With no eligible players, take the native no-distance-rejection branch.
__declspec(naked) static void SpawnDistancePlayer(){__asm {
    pushfd
    pushad
    mov ebp,esp
    and esp,0fffffff0h
    sub esp,512
    fxsave [esp]
    fninit
    fldcw word ptr [esp]
    lea eax,[ebx+4bch]
    push eax
    call SpawnNearest
    add esp,4
    test eax,eax
    jz noPlayers
    mov dword ptr [ebp+28],eax
    jmp restoreState
noPlayers:
    mov eax,spawnDistancePassed
    mov dword ptr [ebp+36],eax
restoreState:
    fxrstor [esp]
    mov esp,ebp
    popad
    popfd
    ret
}}

__declspec(noinline) static int CallEcl(unsigned char* enemy,float step) {
    int result;
    __asm {
        mov edi,enemy
        push step
        call eclOriginal
        mov result,eax
    }
    return result;
}
static int __cdecl RunEcl(unsigned char* enemy,float step) {
    Scope scope((const float*)(enemy+0x1074),false);
    return CallEcl(enemy,step);
}
// Native convention: EDI=Enemy*, one stack float, callee pops 4; ECX preserved.
__declspec(naked) static void EclHook(){__asm {
    push ecx
    push dword ptr [esp+8]
    push edi
    call RunEcl
    add esp,8
    pop ecx
    ret 4
}}

__declspec(noinline) static int CallVolley(unsigned char* shooter) {
    int result;
    __asm {
        mov esi,shooter
        call volleyOriginal
        mov result,eax
    }
    return result;
}
static int __cdecl RunVolley(unsigned char* shooter) {
    Scope scope((const float*)(shooter+4),true);
    return CallVolley(shooter);
}
// Native convention: ESI=BulletShooter*, no stack args, EAX result.
__declspec(naked) static void VolleyHook(){__asm {
    push esi
    call RunVolley
    add esp,4
    ret
}}

static float __fastcall AngleHook(const float* origin,void*) {
    // Item attraction has already selected its recipient. Preserve that actor
    // for its three native helper calls, rather than independently retargeting.
    const uintptr_t caller=(uintptr_t)_ReturnAddress();
    if(caller==coop_game::Code(0x425f3e)||caller==coop_game::Code(0x425fff)||caller==coop_game::Code(0x426192)) {
        Scope recipient(mem<unsigned char*>(PLAYER));
        return ((float(__fastcall*)(const float*,void*))angleOriginal)(origin,nullptr);
    }
    Scope scope(origin,true);
    return ((float(__fastcall*)(const float*,void*))angleOriginal)(origin,nullptr);
}

static void RegisterLoad(coop_patch::Registry& registry,uintptr_t address100a,
                         const unsigned char* expected,size_t count,void* loader) {
    unsigned char replacement[6]={0xe8,0,0,0,0,0x90};
    const int32_t displacement=(int32_t)((uintptr_t)loader-coop_game::Code(address100a)-5);
    memcpy(replacement+1,&displacement,sizeof(displacement));
    registry.Bytes100a(address100a,expected,replacement,count);
}

static void Register(coop_patch::Registry& registry) {
    static const unsigned char ecl[]={0x51,0x53,0x55,0x56,0x8d,0xb7,0x30,0x10,0,0};
    static const unsigned char volley[]={0x83,0xec,0x08,0x53,0x55};
    static const unsigned char angle[]={0x55,0x8b,0xec,0x83,0xe4,0xf8};
    static const unsigned char eax[]={0xa1,0x14,0x45,0x4b,0};
    static const unsigned char ecx[]={0x8b,0x0d,0x14,0x45,0x4b,0};
    static const unsigned char edx[]={0x8b,0x15,0x14,0x45,0x4b,0};
    spawnDistancePassed=coop_game::Code(0x40a6f0);
    registry.Hook100a(0x468d70,ecl,sizeof(ecl),(void*)EclHook,&eclOriginal);
    registry.Hook100a(0x40b5c0,volley,sizeof(volley),(void*)VolleyHook,&volleyOriginal);
    registry.Hook100a(0x437780,angle,sizeof(angle),(void*)AngleHook,&angleOriginal);
    static const uintptr_t eaxSites[]={0x41760a,0x4193cf,0x419aa8,0x419d34,0x41a092,0x41a3e2,0x437789};
    static const uintptr_t ecxSites[]={0x419abc,0x41a0a1,0x41a22a};
    for(const uintptr_t site:eaxSites)
        RegisterLoad(registry,site,eax,sizeof(eax),(void*)LoadEax);
    for(const uintptr_t site:ecxSites)
        RegisterLoad(registry,site,ecx,sizeof(ecx),(void*)LoadEcx);
    RegisterLoad(registry,0x41a23a,edx,sizeof(edx),(void*)LoadEdx);
    RegisterLoad(registry,0x40a6a0,eax,sizeof(eax),(void*)SpawnDistancePlayer);
}

static void Reset(){target=nullptr;depth=0;}
static uint32_t StateHash(){return (depth<<2)|(target==first&&target?1u:target==second&&target?2u:0u);}
}
