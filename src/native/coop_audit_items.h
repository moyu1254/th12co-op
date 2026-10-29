#pragma once
// Smoke DLL only. Each case mutates an isolated, disposable game's live native
// objects, invokes the original item spawn/update code, and then must be closed.
#if defined(COOP_SMOKE_TEST)
namespace coop_item_audit {
static unsigned char* Manager(){return mem<unsigned char*>(0x4b44f0);}
static void ClearItems(){
 auto manager=Manager();
 for(size_t n=0;n<2664;n++)mem<int>((uintptr_t)manager+0x14+n*0x9d8+0x9b0)=0;
 coop_items::Reset();
}
static void Place(unsigned char* p,float x,float y){
 auto a=(uintptr_t)p;
 float dx=x-mem<float>(a+0x97c),dy=y-mem<float>(a+0x980);
 for(auto offset:{0xc444u,0xc45cu,0xc474u}){
  mem<float>(a+offset)+=dx;mem<float>(a+offset+4)+=dy;
  mem<float>(a+offset+12)+=dx;mem<float>(a+offset+16)+=dy;
 }
 mem<float>(a+0x97c)=x;mem<float>(a+0x980)=y;
 mem<int>(a+0x988)=(int)(x*128);mem<int>(a+0x98c)=(int)(y*128);
}
static unsigned char* Spawn(int type,float x,float y){
 float position[3]={x,y,0};
 auto p=((unsigned char*(__stdcall*)(int,float*,float,float))coop_game::Code(0x4273d0))(type,position,0.f,0.f);
 auto begin=(uintptr_t)Manager()+0x14,delta=(uintptr_t)p-begin;
 if(!p||delta%0x9d8||delta/0x9d8>=2664||mem<int>((uintptr_t)p+0x9b0)==0)return nullptr;
 mem<float>((uintptr_t)p+0x978)=mem<float>((uintptr_t)p+0x97c)=mem<float>((uintptr_t)p+0x980)=0.f;
 return p;
}
static void Step(uint32_t p1=0,uint32_t p2=0){
 previous[0]=p1;previous[1]=p2;
 coop_items::Tick(Manager(),nullptr,itemOrig,actorEligible);
}
static uint32_t State(unsigned char* p){return mem<uint32_t>((uintptr_t)p+0x9b0);}
static uint32_t Owner(unsigned char* p){return coop_items::owners[((uintptr_t)p-(uintptr_t)Manager()-0x14)/0x9d8];}
static uint32_t Bits(float v){uint32_t r;memcpy(&r,&v,4);return r;}
static void InventoryClear(){for(uintptr_t a=0x4b0c4c;a<=0x4b0c64;a+=4)mem<uint32_t>(a)=0;}
static void Timer(unsigned char* item,size_t offset,int value){
 mem<int>((uintptr_t)item+offset)=value-1;
 mem<int>((uintptr_t)item+offset+4)=value;
 mem<float>((uintptr_t)item+offset+8)=(float)value;
}
// The retail UFO constructor takes its manager in ESI and color in EAX.
// Keep this custom ABI behind a compiler boundary: native x87/trig routines
// may clobber XMM registers which an inlined MSVC caller would keep live.
__declspec(noinline) static bool SpawnUfo(int color){
 auto manager=mem<uintptr_t>(0x4b4534);auto target=coop_game::Code(0x44a880);
 __asm {
  push esi
  mov esi,manager
  mov eax,color
  call target
  pop esi
 }
 return mem<uint32_t>(manager+0x2c)!=0&&mem<uintptr_t>(manager+0x34)!=0;
}
static unsigned char* Food(int type){
 auto ufo=mem<uintptr_t>(0x4b4534),enemy=mem<uintptr_t>(ufo+0x34);
 return Spawn(type,mem<float>(enemy+0x1074),mem<float>(enemy+0x1078));
}
static uint32_t CountType(int type){
 uint32_t count=0;auto manager=(uintptr_t)Manager();
 for(size_t n=0;n<2664;n++){
  auto item=manager+0x14+n*0x9d8;
  if(mem<int>(item+0x9b0)&&mem<int>(item+0x9b4)==type)++count;
 }
 return count;
}
static unsigned char* FindType(int type){
 auto manager=(uintptr_t)Manager();
 for(size_t n=0;n<2664;n++){
  auto item=manager+0x14+n*0x9d8;
  if(mem<int>(item+0x9b0)&&mem<int>(item+0x9b4)==type)return (unsigned char*)item;
 }
 return nullptr;
}
static bool CurrentFingerprint(uint64_t& value){
 // Offline fixtures have no network mapping. Exercise the actual final hash
 // with temporary output storage, without enabling lockstep or waiting on it.
 auto saved=coop_lockstep::state;static coop_lockstep::Shared temporary;
 memset(&temporary,0,sizeof(temporary));coop_lockstep::state=&temporary;
 coop_lockstep::Digest digest={};value=coop_lockstep::Fingerprint(first,second,digest);
 bool valid=temporary.h.status!=4;coop_lockstep::state=saved;return valid;
}
}
static uint32_t RunItemAudit(uint32_t caseId,uint32_t* details){
 using namespace coop_item_audit;
 memset(details,0,32*sizeof(uint32_t));details[0]=caseId;
 if(!first||!second||!Manager()||!itemOrig||mem<int>((uintptr_t)first+0xa28)!=1||mem<int>((uintptr_t)second+0xa28)!=1)return 0x80000000u;
 const auto ufo=mem<uintptr_t>(0x4b4534),gui=mem<uintptr_t>(0x4b43e4),game=mem<uintptr_t>(0x4b44e8);
 if(!ufo||!gui||!game||mem<int>(ufo+0x2c)||mem<int>(gui+0x6d30)||(mem<uint32_t>(game+0x60)&0x405))return 0x80000001u;
 ClearItems();InventoryClear();coop_resources::Reset(first,second);
 uint32_t failure=0;unsigned char* item=nullptr;
 if(caseId==1){
  Place(first,50,300);Place(second,0,100);item=Spawn(2,0,300);if(!item)return 0x40000000;
  Step();details[1]=State(item);details[2]=Owner(item);details[3]=Bits(mem<float>((uintptr_t)item+0x97c));
  if(State(item)!=3)failure|=1;if(Owner(item)!=2)failure|=2;if(mem<float>((uintptr_t)item+0x97c)>=0)failure|=4;
 }else if(caseId==2){
  Place(first,-150,300);Place(second,0,300);
  float normal=mem<float>((uintptr_t)second+0xc480),focus=mem<float>((uintptr_t)second+0xc468);
  float x=(normal+focus)*.5f;details[1]=Bits(normal);details[2]=Bits(focus);details[3]=Bits(x);
  if(focus<=normal)return 0x80000002u;
  item=Spawn(2,x,300);if(!item)return 0x40000000;Step(0,8);
  details[4]=State(item);details[5]=Owner(item);if(State(item)!=4||Owner(item)!=2)failure|=1;
  ClearItems();item=Spawn(2,x,300);if(!item)return 0x40000000;Step(8,0);
  details[6]=State(item);details[7]=Owner(item);if(State(item)!=1||Owner(item)!=0)failure|=2;
 }else if(caseId==3){
  Place(first,0,300);
  float rx=mem<float>((uintptr_t)first+0xc450),ry=mem<float>((uintptr_t)first+0xc454)-300.f;
  float ix=.99f*rx,iy=300.f+.99f*ry;
  Place(second,ix+1.01f*rx,iy);
  item=Spawn(1,ix,iy);if(!item)return 0x40000000;
  float da=coop_items::Distance(item,first),db=coop_items::Distance(item,second);
  details[1]=Bits(da);details[2]=Bits(db);
  if(!(db<da)||!coop_items::Contains(item,first,0xc444)||coop_items::Contains(item,second,0xc444))return 0x80000003u;
  mem<int>(0x4b0c48)=0;Step();details[3]=State(item);details[4]=mem<uint32_t>(0x4b0c48);
  if(State(item)!=0)failure|=1;if(mem<int>(0x4b0c48)!=1)failure|=2;
 }else if(caseId==4){
  Place(first,-100,100);Place(second,100,300);item=Spawn(2,0,300);if(!item)return 0x40000000;
  Step();details[1]=Owner(item);if(Owner(item)!=1||State(item)!=3)failure|=1;
  Place(first,-150,100);Place(second,0,260);Step();details[2]=Owner(item);details[3]=Bits(mem<float>((uintptr_t)item+0x978));
  if(Owner(item)!=1||mem<float>((uintptr_t)item+0x978)>=0)failure|=2;
  mem<int>((uintptr_t)first+0xa28)=2;Place(second,100,100);Step();details[4]=Owner(item);details[5]=Bits(mem<float>((uintptr_t)item+0x978));
  if(Owner(item)!=2||mem<float>((uintptr_t)item+0x978)<=0)failure|=4;
 }else if(caseId==5){
  Place(first,0,300);Place(second,0,300);mem<int>((uintptr_t)first+0xa28)=mem<int>((uintptr_t)second+0xa28)=2;
  item=Spawn(13,0,300);if(!item)return 0x40000000;
  for(auto offset:{0x988u,0x99cu}){mem<int>((uintptr_t)item+offset)=49;mem<int>((uintptr_t)item+offset+4)=50;mem<float>((uintptr_t)item+offset+8)=50.f;}
  Step();details[1]=State(item);details[2]=mem<uint32_t>(0x4b0c58);details[3]=mem<uint32_t>((uintptr_t)item+0x9c4);details[4]=mem<uint32_t>((uintptr_t)item+0x98c);details[5]=mem<uint32_t>((uintptr_t)item+0x9a0);
  if(State(item)!=6||mem<int>(0x4b0c58)!=0)failure|=1;if(mem<int>((uintptr_t)item+0x9c4)!=0)failure|=2;
  if(mem<int>((uintptr_t)item+0x98c)!=51||mem<int>((uintptr_t)item+0x9a0)!=51)failure|=4;
 }else if(caseId==6){
  Place(first,0,300);Place(second,0,300);mem<int>(0x4b0c48)=0;
  item=Spawn(1,0,300);if(!item)return 0x40000000;Step();details[1]=State(item);details[2]=mem<uint32_t>(0x4b0c48);
  Step();details[3]=mem<uint32_t>(0x4b0c48);if(State(item)!=0||details[2]!=1||details[3]!=1)failure|=1;
  item=Spawn(13,0,300);if(!item)return 0x40000000;Step();details[4]=State(item);details[5]=mem<uint32_t>(0x4b0c58);details[6]=mem<uint32_t>(0x4b0c4c);
  Step();details[7]=mem<uint32_t>(0x4b0c58);if(State(item)!=0||details[5]!=1||details[6]!=1||details[7]!=1)failure|=2;
 }else if(caseId==7){
  Place(first,0,100);Place(second,0,100);mem<int>((uintptr_t)first+0xa28)=mem<int>((uintptr_t)second+0xa28)=2;
  item=Spawn(2,0,300);auto cancel=Spawn(9,50,300);if(!item||!cancel)return 0x40000000;
  mem<int>((uintptr_t)cancel+0x9b0)=2;Step();details[1]=State(item);details[2]=State(cancel);details[3]=Owner(item);details[4]=Owner(cancel);
  if(State(item)!=1||Owner(item))failure|=1;if(State(cancel)!=2||Owner(cancel))failure|=2;
 }else if(caseId==8){
  // Exercise the installed native transition entry, not a replacement model.
  mem<uint32_t>((uintptr_t)first+0xc414)&=~8u;mem<uint32_t>((uintptr_t)second+0xc414)&=~8u;
  mem<uint32_t>((uintptr_t)first+0xc418)=mem<uint32_t>((uintptr_t)second+0xc418)=7;
  auto old=singleton();singleton()=first;((void(__cdecl*)())coop_game::Code(0x41d000))();singleton()=old;
  details[1]=mem<uint32_t>((uintptr_t)first+0xc414);details[2]=mem<uint32_t>((uintptr_t)second+0xc414);
  details[3]=mem<uint32_t>((uintptr_t)first+0xc418);details[4]=mem<uint32_t>((uintptr_t)second+0xc418);
  if(!(details[1]&8)||!(details[2]&8)||details[3]||details[4])failure|=1;
  Place(first,100,300);Place(second,-100,430);mem<uint32_t>(gui+0x6d18)&=~1u;
  coop_progression::SelectLower((unsigned char*)gui);details[5]=coop_progression::lowerActor==second;
  Place(second,-100,405);mem<uint32_t>(gui+0x6d18)|=1u;
  coop_progression::SelectLower((unsigned char*)gui);details[6]=coop_progression::lowerActor==second;
  Place(second,-100,390);coop_progression::SelectLower((unsigned char*)gui);details[7]=coop_progression::lowerActor==coop_progression::clearPosition;
  if(!details[5]||!details[6]||!details[7])failure|=2;
  Place(second,-100,50);mem<uint32_t>(gui+0x6d18)&=~8u;
  details[8]=coop_progression::SelectUpper((unsigned char*)gui)==second;
  Place(second,-1,79);mem<uint32_t>(gui+0x6d18)|=8u;
  details[9]=coop_progression::SelectUpper((unsigned char*)gui)==second;
  Place(second,1,81);details[10]=coop_progression::SelectUpper((unsigned char*)gui)==coop_progression::clearPosition;
  if(!details[8]||!details[9]||!details[10])failure|=4;
  Place(second,100,50);coop_progression::SelectSpell();details[11]=coop_progression::spellActor==second;
  mem<int>((uintptr_t)second+0xa28)=2;coop_progression::SelectSpell();details[12]=coop_progression::spellActor==first;
  mem<int>((uintptr_t)first+0xa28)=2;coop_progression::SelectSpell();details[13]=coop_progression::spellActor==coop_progression::clearPosition;
  if(!details[11]||!details[12]||!details[13])failure|=8;
  auto score=mem<unsigned char*>(0x4b451c);if(!score)return 0x80000008u;
  unsigned char saved[0x1edfc];memcpy(saved,score,sizeof(saved));coop_progression::RefreshMenuAccess();
  details[14]=memcmp(saved,score,sizeof(saved))==0;details[15]=coop_progression::menuScore==coop_progression::menuAccessView;
  for(size_t shot=0;shot<6;shot++){
   if(coop_progression::menuScore[0x1e9d0+shot]&0x10)++details[16];
   for(size_t rank=0;rank<4;rank++)for(size_t stage=0;stage<6;stage++)
    if(coop_progression::menuScore[shot*0x45f4+rank*48+stage*8+0x5b1])++details[17];
  }
  // Execute the installed native availability helper used to construct the
  // title cursor's disabled list; checking the view bytes alone missed it.
  details[18]=((uint32_t(__cdecl*)())coop_game::Code(0x43f160))();
  details[19]=memcmp(saved,score,sizeof(saved))==0;
  if(!details[14]||!details[15]||details[16]!=6||details[17]!=144||details[18]!=1||!details[19])failure|=16;
 }else if(caseId==9){
  // A changing token freezes near P2 alone, then awards one shared token.
  // Proximity (native radius 60) and physical pickup must both use P2.
  Place(first,-150,350);Place(second,150,350);
  item=Spawn(13,150,300);if(!item)return 0x40000000;
  Timer(item,0x988,50);Timer(item,0x99c,50);Step();
  details[1]=State(item);details[2]=mem<uint32_t>((uintptr_t)item+0x9c4);
  details[3]=mem<uint32_t>((uintptr_t)item+0x98c);details[4]=mem<uint32_t>((uintptr_t)item+0x9a0);
  details[5]=mem<uint32_t>(0x4b0c58);
  if(details[1]!=6||details[2]!=1||details[3]!=50||details[4]!=50||details[5])failure|=1;
  Place(second,mem<float>((uintptr_t)item+0x96c),mem<float>((uintptr_t)item+0x970));Step();
  details[6]=State(item);details[7]=mem<uint32_t>(0x4b0c58);details[8]=mem<uint32_t>(0x4b0c4c);
  Step();details[9]=mem<uint32_t>(0x4b0c58);
  if(details[6]||details[7]!=1||details[8]!=1||details[9]!=1)failure|=2;
 }else if(caseId==10){
  // An item near the stage origin is not near either actor. Neither an
  // origin-based target nor an ineligible nearby actor may freeze its color.
  Place(first,-150,350);Place(second,150,350);
  item=Spawn(13,0,32);if(!item)return 0x40000000;
  Timer(item,0x988,50);Timer(item,0x99c,50);Step();
  details[1]=State(item);details[2]=mem<uint32_t>((uintptr_t)item+0x9c4);
  details[3]=mem<uint32_t>((uintptr_t)item+0x98c);details[4]=mem<uint32_t>((uintptr_t)item+0x9a0);
  if(details[1]!=6||details[2]||details[3]!=51||details[4]!=51)failure|=1;
  ClearItems();Place(first,0,300);mem<int>((uintptr_t)first+0xa28)=2;
  item=Spawn(14,0,300);if(!item)return 0x40000000;
  Timer(item,0x988,50);Timer(item,0x99c,50);Step();
  details[5]=State(item);details[6]=mem<uint32_t>((uintptr_t)item+0x9c4);
  details[7]=mem<uint32_t>((uintptr_t)item+0x9a0);details[8]=mem<uint32_t>(0x4b0c58);
  if(details[5]!=6||details[6]||details[7]!=51||details[8])failure|=2;
  mem<int>((uintptr_t)first+0xa28)=1;Place(second,0,300);Step();
  details[9]=State(item);details[10]=mem<uint32_t>(0x4b0c58);details[11]=mem<uint32_t>(0x4b0c4c);
  if(details[9]||details[10]!=1||details[11]!=2)failure|=4;
 }else if(caseId==11){
  // Native states 3, 4 and 8 all retain a living owner, transfer immediately
  // from a deathbomb-window owner, and never inherit a recycled slot's owner.
  const int states[]={3,4,8};
  for(size_t n=0;n<3;n++){
   ClearItems();mem<int>((uintptr_t)first+0xa28)=mem<int>((uintptr_t)second+0xa28)=1;
   Place(first,-60,300);Place(second,150,300);
   item=Spawn(states[n]==8?16:2,0,300);if(!item)return 0x40000000;
   mem<int>((uintptr_t)item+0x9b0)=states[n];Timer(item,0x988,2);
   mem<float>((uintptr_t)item+0x9bc)=mem<float>((uintptr_t)item+0x984)=1.f;
   Step();details[1+n*3]=Owner(item);
   Place(first,-180,300);Place(second,50,300);Step();details[2+n*3]=Owner(item);
   if(Owner(item)!=1||State(item)!=(uint32_t)states[n]||mem<float>((uintptr_t)item+0x978)>=0.f)failure|=1u<<(n*2);
   mem<int>((uintptr_t)first+0xa28)=4;Step();details[3+n*3]=Owner(item);
   if(Owner(item)!=2||State(item)!=(uint32_t)states[n]||mem<float>((uintptr_t)item+0x978)<=0.f)failure|=2u<<(n*2);
  }
  // The last reward slot still records owner 2, but native spawn initializes
  // the same slot's timer. That spawn boundary must select owner 1 afresh.
  auto recycled=item;mem<int>((uintptr_t)item+0x9b0)=0;
  mem<int>((uintptr_t)first+0xa28)=1;Place(first,-150,300);Place(second,150,300);
  item=Spawn(16,-90,300);if(!item||item!=recycled)return 0x8000000bu;
  Step();details[10]=Owner(item);details[11]=State(item);
  if(Owner(item)!=1||State(item)!=8)failure|=64;
 }else if(caseId==12){
  // Releasing attraction while both actors are ineligible must preserve a
  // large-UFO reward's payload; revival collects that physical reward once.
  Place(first,-100,300);Place(second,100,300);item=Spawn(16,0,300);if(!item)return 0x40000000;
  mem<int>((uintptr_t)item+0x9c8)=1;mem<int>((uintptr_t)item+0x9cc)=3;
  mem<int>((uintptr_t)item+0x9d0)=2;mem<float>((uintptr_t)item+0x9d4)=1.f;
  mem<int>(0x4b0c48)=0;Timer(item,0x988,2);Step();
  mem<int>((uintptr_t)first+0xa28)=mem<int>((uintptr_t)second+0xa28)=4;Step();
  details[1]=State(item);details[2]=Owner(item);details[3]=mem<uint32_t>((uintptr_t)item+0x9c8);
  details[4]=mem<uint32_t>((uintptr_t)item+0x9cc);details[5]=mem<uint32_t>((uintptr_t)item+0x9d0);
  details[6]=mem<uint32_t>((uintptr_t)item+0x9d4);details[7]=mem<uint32_t>(0x4b0c48);
  if(details[1]!=1||details[2]||details[3]!=1||details[4]!=3||details[5]!=2||details[6]!=Bits(1.f)||details[7])failure|=1;
  mem<int>((uintptr_t)second+0xa28)=1;
  Place(second,mem<float>((uintptr_t)item+0x96c),mem<float>((uintptr_t)item+0x970));
  Step();details[8]=State(item);details[9]=mem<uint32_t>(0x4b0c48);
  details[10]=mem<uint32_t>(0x4b0c44);Step();details[11]=mem<uint32_t>(0x4b0c48);
  details[12]=mem<uint32_t>(0x4b0c44);
  if(details[8]||details[9]!=3||details[11]!=3||details[10]!=details[12])failure|=2;
 }else if(caseId==13){
  // Use actual UFO spawn + actual state1->7 absorption, including the native
  // priority over overlapping actors and absorption while neither can collect.
  Place(first,-150,400);Place(second,150,400);
  if(!SpawnUfo(1))return 0x8000000du;
  Timer((unsigned char*)ufo,0x10,60);
  const int savedStage=mem<int>(0x4b0cb0),stages[]={1,6,7};
  for(size_t n=0;n<3;n++){
   mem<int>(0x4b0cb0)=stages[n];mem<int>(ufo+0x48)=mem<int>(ufo+0x4c)=0;mem<float>(ufo+0x50)=0.f;
   if(n==1){auto enemy=mem<uintptr_t>(ufo+0x34);Place(first,mem<float>(enemy+0x1074),mem<float>(enemy+0x1078));Place(second,mem<float>(enemy+0x1074),mem<float>(enemy+0x1078));}
   if(n==2)mem<int>((uintptr_t)first+0xa28)=mem<int>((uintptr_t)second+0xa28)=4;
   auto power=Food(1),point=Food(2);if(!power||!point){mem<int>(0x4b0cb0)=savedStage;return 0x40000000;}
   Step();const size_t d=1+n*7;
   details[d]=stages[n];details[d+1]=mem<uint32_t>(ufo+0x48);details[d+2]=mem<uint32_t>(ufo+0x4c);
   details[d+3]=Bits(mem<float>(ufo+0x50));details[d+4]=State(power);details[d+5]=State(point);
   float expected=(float)(2.0*(mem<double>(0x4a4350)-stages[n]*mem<double>(0x4a3fa0)));
   if(details[d+1]!=1||details[d+2]!=1||details[d+4]||details[d+5]||fabsf(mem<float>(ufo+0x50)-expected)>0.0000002f)failure|=1u<<(n*2);
   Step();details[d+6]=mem<uint32_t>(ufo+0x48)+mem<uint32_t>(ufo+0x4c);
   if(details[d+6]!=2||fabsf(mem<float>(ufo+0x50)-expected)>0.0000002f)failure|=2u<<(n*2);
  }
  mem<int>(0x4b0cb0)=savedStage;
 }else if(caseId>=14&&caseId<=17){
  // The original fullness threshold issues a color-specific bonus once.
  // Red: life fragment; green: bomb; blue: no extra item; rainbow: token.
  const int colors[]={1,3,2,-1},bonuses[]={4,7,0,13};
  const auto index=caseId-14;const int color=colors[index],bonus=bonuses[index];
  Place(first,-150,400);Place(second,150,400);mem<int>(0x4b0c54)=1;
  if(!SpawnUfo(color))return 0x8000000eu;
  Timer((unsigned char*)ufo,0x10,60);mem<float>(ufo+0x50)=.99f;
  item=Food(1);if(!item)return 0x40000000;Step();
  details[1]=(uint32_t)color;details[2]=Bits(mem<float>(ufo+0x50));
  details[3]=mem<uint32_t>(ufo+0x48);details[4]=mem<uint32_t>(ufo+0x4c);
  details[5]=CountType(4);details[6]=CountType(7);details[7]=CountType(13);
  if(mem<float>(ufo+0x50)!=1.f||details[3]!=(color==-1?0u:1u)||details[4]!=(color==-1?1u:0u))failure|=1;
  if(details[5]!=(bonus==4?1u:0u)||details[6]!=(bonus==7?1u:0u)||details[7]!=(bonus==13?1u:0u))failure|=2;
  item=Food(1);if(!item)return 0x40000000;Step();Step();
  details[8]=mem<uint32_t>(ufo+0x48);details[9]=mem<uint32_t>(ufo+0x4c);
  details[10]=CountType(4);details[11]=CountType(7);details[12]=CountType(13);
  if(details[8]!=(color==-1?0u:2u)||details[9]!=(color==-1?2u:0u)||mem<float>(ufo+0x50)!=1.f)failure|=4;
  if(details[10]!=details[5]||details[11]!=details[6]||details[12]!=details[7])failure|=8;
 }else if(caseId==18){
  // Invoke the original UFO payout routine once after genuine absorption.
  // This tests the payout/collection path, not the enemy HP/death scheduler.
  Place(first,-150,400);Place(second,150,400);mem<int>(0x4b0c48)=0;
  if(!SpawnUfo(1))return 0x80000012u;Timer((unsigned char*)ufo,0x10,60);
  for(int n=0;n<3;n++){if(!Food(1))return 0x40000000;Step();}
  for(int n=0;n<2;n++){if(!Food(2))return 0x40000000;Step();}
  details[1]=mem<uint32_t>(ufo+0x48);details[2]=mem<uint32_t>(ufo+0x4c);
  ((void(__cdecl*)())coop_game::Code(0x44aee0))();
  item=FindType(18);details[3]=CountType(18);details[4]=CountType(4);details[5]=CountType(13);
  if(!item)return failure|0x100;
  details[6]=mem<uint32_t>((uintptr_t)item+0x9c8);details[7]=mem<uint32_t>((uintptr_t)item+0x9cc);
  details[8]=mem<uint32_t>((uintptr_t)item+0x9d0);details[9]=Bits(mem<float>((uintptr_t)item+0x9d4));
  if(details[1]!=3||details[2]!=2||details[3]!=1||details[4]!=1||details[5]!=1||details[6]!=1||details[7]!=3||details[8]!=2)failure|=1;
  Place(second,mem<float>((uintptr_t)item+0x96c),mem<float>((uintptr_t)item+0x970));Step();
  details[10]=State(item);details[11]=mem<uint32_t>(0x4b0c48);details[12]=mem<uint32_t>(0x4b0c44);
  Step();details[13]=mem<uint32_t>(0x4b0c48);details[14]=mem<uint32_t>(0x4b0c44);
  if(details[10]||details[11]!=3||details[13]!=3||details[12]!=details[14])failure|=2;
 }else if(caseId==19){
  // Future-impacting native fields must change the ACTUAL frame fingerprint,
  // even before their different motion/color/award reaches visible globals.
  Place(first,-150,400);Place(second,150,400);mem<int>(0x4b0c48)=0;
  if(!SpawnUfo(1))return 0x20000000u;Timer((unsigned char*)ufo,0x10,60);
  for(int n=0;n<3;n++){if(!Food(1))return 0x40000000;Step();}
  for(int n=0;n<2;n++){if(!Food(2))return 0x40000000;Step();}
  ((void(__cdecl*)())coop_game::Code(0x44aee0))();
  auto reward=(uintptr_t)FindType(18),token=(uintptr_t)FindType(13);
  if(!reward||!token)return 0x20000000u;
  const uintptr_t fields[]={reward+0x984,token+0x99c,token+0x9a0,token+0x9a4,
   reward+0x9c8,reward+0x9cc,reward+0x9d0,reward+0x9d4,
   ufo+0x10,ufo+0x14,ufo+0x18,ufo+0x28,ufo+0x2c,ufo+0x30,
   ufo+0x48,ufo+0x4c,ufo+0x50,0x4b0c5c,0x4b0c60,0x4b0c64};
  const uint32_t changes[]={0x3f800000,0x100,0x100,0x100,2,1,1,0x00800000,
   0x100,0x100,0x100,0x100,1,2,1,1,0x00800000,1,1,1};
  uint64_t baseline=0,value=0;
  if(!CurrentFingerprint(baseline))return 0x80000013u;
  for(unsigned n=0;n<sizeof(fields)/sizeof(fields[0]);++n){
   auto& field=mem<uint32_t>(fields[n]);auto saved=field;field^=changes[n];
   bool valid=CurrentFingerprint(value);details[1+n]=valid&&value!=baseline;
   if(!details[1+n])failure|=1u<<n;
   field=saved;
   if(!CurrentFingerprint(value)||value!=baseline)failure|=0x40000000u;
  }
 }else return 0x800000ffu;
 details[30]=(uint32_t)coop_items::DeterministicStateHash();details[31]=(uint32_t)(coop_items::DeterministicStateHash()>>32);
 return failure;
}
#endif
