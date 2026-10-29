#pragma once

// Destructive campaign fixture, compiled only into th12coop-smoke.dll.
// Include after coop_resources.h and the native actor/options declarations.
// Configure and BeforeFrame must run on the game thread before P1's tick.
// Close the disposable process after use; this is not a production game mode.
namespace coop_campaign {
#ifdef COOP_SMOKE_TEST
static bool active;
static uint32_t damageMultiplier=1,stage,frames,refreshes;
static unsigned char *configuredFirst,*configuredSecond;
static uint32_t* report;
static uint32_t Bits(float value){uint32_t word;memcpy(&word,&value,4);return word;}
static bool Active(){return active;}
static void RefreshOptions(){
 auto old=singleton();
 mem<int>(0x4b0c48)=400;
 auto update=(void(__stdcall*)(void*))optionsOrig;
 singleton()=first;update(first);
 singleton()=second;update(second);
 singleton()=old;
 configuredFirst=first;configuredSecond=second;
 stage=mem<uint32_t>(0x4b0cb0);++refreshes;
}
static void BeforeFrame(){
 if(!active||!first||!second)return;
 // Actor objects may be recreated for a new stage. Refresh once per actor
 // pair/stage, using the native option updater, not synthetic option objects.
 if(first!=configuredFirst||second!=configuredSecond||stage!=mem<uint32_t>(0x4b0cb0))RefreshOptions();
 for(auto actor:{first,second}){
  const int state=mem<int>((uintptr_t)actor+0xa28);
  if(state==0||state==1)coop_resources::Invulnerable(actor,60);
 }
 // Keep the native timer speed unchanged: stage initialization contains
 // exact-frame gates that must advance through every normal game frame.
 ++frames;
 if(report){report[5]=stage;report[6]=frames;report[7]=refreshes;}
}
static uint32_t Configure(uint32_t caseId,uint32_t* details){
 if(!details)return 0x80000000u;
 memset(details,0,32*sizeof(uint32_t));details[0]=caseId;
 if(caseId!=1&&caseId!=2)return 0x20000000u;
 if(active||!first||!second||!optionsOrig||!coop_resources::invulnOriginal||
    mem<int>((uintptr_t)first+0xa28)!=1||mem<int>((uintptr_t)second+0xa28)!=1)return 0x80000001u;
 damageMultiplier=caseId==1?4u:1u;
 details[1]=Bits(mem<float>(0x4b2ed0));details[2]=400;details[3]=60;details[4]=damageMultiplier;
 details[8]=mem<uint32_t>(0x4b0c48);details[9]=Bits(mem<float>(0x4b2ed0));
 report=details;frames=refreshes=0;configuredFirst=configuredSecond=nullptr;
 active=true;BeforeFrame();return 0;
}
static int ScaleDamage(int damage){
 if(!active||damage<=0||damageMultiplier==1)return damage;
 return damage>0x7fffffff/(int)damageMultiplier?0x7fffffff:damage*(int)damageMultiplier;
}
#else
static bool Active(){return false;}
static void BeforeFrame(){}
static int ScaleDamage(int damage){return damage;}
#endif
}
