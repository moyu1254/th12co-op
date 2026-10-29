#pragma once
#ifdef COOP_SMOKE_TEST
// Fixture-only code. Call on the game thread with two real, initialized actors.
// Each case changes its fixture and must be followed by closing that owned game.
namespace coop_audit_targeting {
static uint32_t failures,assertions;
static uint32_t* details;
static void Check(bool value,unsigned bit){++assertions;if(!value)failures|=1u<<bit;}
static uint32_t Bits(float value){uint32_t result;memcpy(&result,&value,4);return result;}
static void Position(unsigned char* p,float x,float y){
    mem<float>((uintptr_t)p+0x97c)=x;mem<float>((uintptr_t)p+0x980)=y;
    mem<int>((uintptr_t)p+0x988)=(int)(x*128.f);mem<int>((uintptr_t)p+0x98c)=(int)(y*128.f);
}
static void Alive(){
    coop_resources::Reset(first,second);
    for(auto p:{first,second}){
        mem<int>((uintptr_t)p+0xa28)=1;
        mem<uint32_t>((uintptr_t)p+0xc414)&=~2u;
        mem<int>((uintptr_t)p+0xc404)=1; // Circle reports an intersection; lasers suppress invulnerable hits.
    }
}
static float Angle(const float* p){return ((float(__fastcall*)(const float*,void*))coop_game::Code(0x437780))(p,nullptr);}
static void Targeting(){
    Alive();coop_targeting::Reset();auto originalOwner=singleton();
    Position(first,-50.f,300.f);Position(second,50.f,300.f);
    float origin[3]={0.f,300.f,0.f};
    Check(coop_targeting::Nearest(origin)==first,0);
    origin[0]=45.f;Check(coop_targeting::Nearest(origin)==second,1);
    float a=Angle(origin);details[1]=Bits(a);Check(fabsf(a)<0.0001f,2);
    origin[0]=-45.f;a=Angle(origin);details[2]=Bits(a);Check(fabsf(fabsf(a)-3.14159265f)<0.0001f,3);
    origin[0]=0.f;
    {
        coop_targeting::Scope ecl(origin,false);
        Check(coop_targeting::target==first,4);
        Position(first,-500.f,300.f);Position(second,5.f,300.f);
        Check(fabsf(fabsf(Angle(origin))-3.14159265f)<0.0001f,5);
        {coop_targeting::Scope volley(origin,true);Check(coop_targeting::target==first,6);}
        {coop_targeting::Scope nestedEnemy(origin,false);Check(coop_targeting::target==second,7);Check(fabsf(Angle(origin))<0.0001f,8);}
        Check(coop_targeting::target==first,9);
    }
    Check(coop_targeting::StateHash()==0,10);
    mem<int>((uintptr_t)first+0xa28)=2;Check(coop_targeting::Nearest(origin)==second,11);
    mem<int>((uintptr_t)second+0xa28)=2;Check(coop_targeting::Nearest(origin)==first,12);
    Check(singleton()==originalOwner,13);
}

alignas(16) static unsigned char savedFx[512],beforeFx[512],afterFx[512];
static unsigned char bulletFixture[0x4c8];
static uint32_t seen[8],beforeFlags,afterFlags,beforeEbp,beforeEsp,skipCount;
static uintptr_t skipResume;
__declspec(naked) static void SkipReturn(){__asm {
    mov skipCount,1
    jmp skipResume
}}
static uint32_t MachineState(void* thunk,uint32_t expectedEax){
    // Static buffers avoid MSVC's EBX-based dynamic stack-alignment frame.
    memset(beforeFx,0,sizeof(beforeFx));memset(afterFx,0,sizeof(afterFx));skipCount=0;
    __asm {
        fxsave savedFx
        fninit
        fld1
        fldpi
        mov eax,offset returned
        mov skipResume,eax
        mov eax,11112222h
        mov ecx,33334444h
        mov edx,55556666h
        lea ebx,bulletFixture
        mov esi,77778888h
        mov edi,9999aaaah
        mov beforeEbp,ebp
        mov beforeEsp,esp
        stc
        pushfd
        pop beforeFlags
        fxsave beforeFx
        call thunk
returned:
        pushfd
        pop afterFlags
        mov seen[0],eax
        mov seen[4],ecx
        mov seen[8],edx
        mov seen[12],ebx
        mov seen[16],esi
        mov seen[20],edi
        mov seen[24],ebp
        mov seen[28],esp
        fxsave afterFx
    }
    uint32_t result=0;
    if(memcmp(beforeFx,afterFx,sizeof(beforeFx)))result|=1;
    if(beforeFlags!=afterFlags)result|=2;
    if(seen[0]!=expectedEax||seen[1]!=0x33334444||seen[2]!=0x55556666||seen[3]!=(uint32_t)(uintptr_t)bulletFixture||
       seen[4]!=0x77778888||seen[5]!=0x9999aaaa||seen[6]!=beforeEbp||seen[7]!=beforeEsp)result|=4;
    __asm {fxrstor savedFx}
    return result;
}
static void Machine(){
    Alive();coop_targeting::Reset();Position(first,-50.f,300.f);Position(second,50.f,300.f);
    mem<float>((uintptr_t)bulletFixture+0x4bc)=48.f;mem<float>((uintptr_t)bulletFixture+0x4c0)=300.f;
    {
        coop_targeting::Scope chosen(second);
        details[1]=MachineState((void*)coop_targeting::LoadEax,(uint32_t)(uintptr_t)second);Check(details[1]==0,0);
    }
    details[2]=MachineState((void*)coop_targeting::SpawnDistancePlayer,(uint32_t)(uintptr_t)second);Check(details[2]==0,1);
    Check(skipCount==0,2);
    mem<int>((uintptr_t)first+0xa28)=2;mem<int>((uintptr_t)second+0xa28)=2;
    const uintptr_t oldSkip=coop_targeting::spawnDistancePassed;coop_targeting::spawnDistancePassed=(uintptr_t)SkipReturn;
    details[3]=MachineState((void*)coop_targeting::SpawnDistancePlayer,0x11112222);coop_targeting::spawnDistancePassed=oldSkip;
    Check(details[3]==0,3);Check(skipCount==1,4);
    details[4]=beforeFlags;details[5]=afterFlags;
}

static uint32_t cancelCount,cancelValid;
static void* cancelLaser;static void* cancelSize;static float cancelX[2];
static void __cdecl RecordCancel(void* laser,float* position,void* size,int mode,int flag){
    if(cancelCount<2)cancelX[cancelCount]=position[0];
    if(laser!=cancelLaser||size!=cancelSize||mode!=7||flag!=9)cancelValid=0;
    ++cancelCount;
}
__declspec(naked) static void CancelFixture(){__asm {
    pushad
    mov ebp,esp
    push dword ptr [ebp+48]
    push dword ptr [ebp+44]
    push dword ptr [ebp+40]
    push dword ptr [ebp+36]
    push ecx
    call RecordCancel
    add esp,20
    popad
    ret 16
}}
__declspec(noinline) static int NativeCircle(void* pos,float radius){int result;uintptr_t entry=coop_game::Code(0x437960);__asm {
    push radius
    mov eax,pos
    call entry
    mov result,eax
}return result;}
static void Collisions(){
    Alive();coop_collision::Reset();Position(first,-100.f,300.f);Position(second,100.f,300.f);
    const uintptr_t gui=mem<uintptr_t>(0x4b43e4);const uintptr_t message=gui?mem<uintptr_t>(gui+0x6d30):0;
    if(gui)mem<uintptr_t>(gui+0x6d30)=0;
    float point[3]={100.f,300.f,0.f};
    details[1]=NativeCircle(point,4.f);Check(details[1]==1,0);Check(coop_collision::hitMask==2,1);Check(coop_collision::effectActor==second,2);
    float beam[3]={80.f,300.f,0.f};
    // Make the geometric fixture independent of the selected shot's SHT size.
    details[4]=Bits(mem<float>((uintptr_t)second+0x9e4));details[5]=Bits(mem<float>((uintptr_t)second+0x9e8));
    for(auto actor:{first,second}){mem<float>((uintptr_t)actor+0x9e4)=2.f;mem<float>((uintptr_t)actor+0x9e8)=2.f;}
    auto oldOwner=singleton();singleton()=first;
    details[6]=coop_collision::LaserOne(coop_collision::geometryOrig,beam,0.f,4.f,40.f);
    singleton()=second;details[7]=coop_collision::LaserOne(coop_collision::geometryOrig,beam,0.f,4.f,40.f);
    singleton()=oldOwner;
    Check(details[6]==0&&details[7]==1,13);
    details[3]=coop_collision::LaserOne((void*)coop_game::Code(0x437ce0),beam,0.f,4.f,40.f);Check(details[3]==1,6);
    details[8]=coop_collision::LaserOne((void*)coop_game::Code(0x437a60),beam,0.f,4.f,40.f);
    Check(details[8]==0,12); // Native laser invulnerability semantics.
    mem<int>((uintptr_t)second+0xc404)=0;
    details[2]=coop_collision::LaserOne((void*)coop_game::Code(0x437a60),beam,0.f,4.f,40.f);
    Check(details[2]==1,3);Check(coop_collision::hitMask==2,4);Check(coop_collision::effectActor==second,5);
    details[9]=mem<int>((uintptr_t)second+0xa28);
    Check(details[9]==4&&mem<int>((uintptr_t)first+0xa28)==1,11); // Native collision starts the deathbomb window.
    float size[3]={2.f,2.f,0.f};cancelLaser=bulletFixture;cancelSize=size;cancelCount=0;cancelValid=1;
    coop_collision::hitMask=3;coop_collision::CancelBoth((void*)CancelFixture,cancelLaser,point,size,7,9);
    Check(cancelCount==2&&cancelValid,7);Check(cancelX[0]==-100.f&&cancelX[1]==100.f,8);
    cancelCount=0;coop_collision::hitMask=2;coop_collision::CancelBoth((void*)CancelFixture,cancelLaser,point,size,7,9);
    Check(cancelCount==1&&cancelValid&&cancelX[0]==100.f,9);
    Check(coop_collision::Result(2,2)==2&&coop_collision::effectActor==first,10);
    if(gui)mem<uintptr_t>(gui+0x6d30)=message;
}

static unsigned char enemyA[0x2800],enemyB[0x2800],field[0x280];
static void* destructorLoader=(void*)coop_homing::DestructorPlayerLoad;
__declspec(naked) static void* __stdcall DestructorLoadFixture(void*){__asm {
    mov eax,dword ptr [esp+4]
    sub esp,2ch
    mov dword ptr [esp+28h],eax
    call destructorLoader
    add esp,2ch
    ret 4
}}
static void FillTargets(){
    for(auto actor:{first,second}){
        mem<void*>((uintptr_t)actor+0x8980)=enemyA;mem<unsigned char>((uintptr_t)actor+0x8984)=1;
        for(unsigned i=0;i<256;++i)mem<void*>((uintptr_t)actor+0xaac+i*0x78)=i==128?enemyB:enemyA;
    }
}
static bool Cleared(){
    for(auto actor:{first,second}){
        if(mem<void*>((uintptr_t)actor+0x8980)||mem<unsigned char>((uintptr_t)actor+0x8984))return false;
        for(unsigned i=0;i<256;++i)if(mem<void*>((uintptr_t)actor+0xaac+i*0x78)!=(i==128?(void*)enemyB:nullptr))return false;
    }
    return true;
}
static void Homing(){
    Alive();Position(first,-40.f,300.f);Position(second,40.f,300.f);
    memset(enemyA,0,sizeof(enemyA));memset(enemyB,0,sizeof(enemyB));
    mem<float>((uintptr_t)enemyA+0x1074)=0.f;mem<float>((uintptr_t)enemyB+0x1074)=-30.f;
    mem<void*>((uintptr_t)enemyA+0x278c)=enemyA;
    for(auto actor:{first,second}){mem<void*>((uintptr_t)actor+0x8980)=enemyB;mem<unsigned char>((uintptr_t)actor+0x8984)=0;}
    coop_homing::SelectPlayers(enemyA+0x1040);
    Check(mem<void*>((uintptr_t)first+0x8980)==enemyA,0);Check(mem<void*>((uintptr_t)second+0x8980)==enemyB,1);
    Check(mem<unsigned char>((uintptr_t)first+0x8984)==1&&mem<unsigned char>((uintptr_t)second+0x8984)==0,2);
    FillTargets();void* owner=DestructorLoadFixture(enemyA);Check(owner==singleton(),3);Check(Cleared(),4);
    FillTargets();uintptr_t entry=coop_game::Code(0x43abd0);
    __asm {
        lea edx,enemyA
        call entry
    }
    Check(Cleared(),5);
}
static void Force(){
    Alive();memset(field,0,sizeof(field));Position(first,-10.f,200.f);Position(second,10.f,200.f);
    mem<float>((uintptr_t)field+0x34)=0.f;mem<float>((uintptr_t)field+0x38)=200.f;
    mem<int>((uintptr_t)field+0x23c)=1;mem<float>((uintptr_t)field+0x258)=2.f;
    auto originalOwner=singleton();auto native=(int(__stdcall*)(void*))coop_game::Code(0x41ae00);
    Check(native(field)==0,0);
    details[1]=Bits(mem<float>((uintptr_t)first+0x97c));details[2]=Bits(mem<float>((uintptr_t)second+0x97c));
    Check(mem<float>((uintptr_t)first+0x97c)==-8.f&&mem<float>((uintptr_t)second+0x97c)==8.f,1);
    details[3]=mem<int>((uintptr_t)first+0x988);details[4]=mem<int>((uintptr_t)second+0x988);
    Check((int)details[3]==-1024&&(int)details[4]==1024,2);
    Check(mem<int>((uintptr_t)first+0x8334)==1&&mem<int>((uintptr_t)second+0x8334)==1,3);
    Check(singleton()==originalOwner,4);
    mem<int>((uintptr_t)first+0xa28)=2;mem<int>((uintptr_t)second+0xa28)=2;
    Check(native(field)==0,5);Check(mem<float>((uintptr_t)first+0x97c)==-8.f&&mem<float>((uintptr_t)second+0x97c)==8.f,6);
}
}
static uint32_t RunTargetingAudit(uint32_t caseId,uint32_t* details32){
    using namespace coop_audit_targeting;
    details=details32;memset(details32,0,32*sizeof(uint32_t));failures=assertions=0;
    if(!first||!second)return 0x80000000u;
    switch(caseId){case 101:Targeting();break;case 102:Machine();break;case 103:Collisions();break;case 104:Homing();break;case 105:Force();break;default:return 0x40000000u;}
    details32[0]=assertions;return failures;
}
#endif
