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
 }else return 0x800000ffu;
 details[30]=(uint32_t)coop_items::DeterministicStateHash();details[31]=(uint32_t)(coop_items::DeterministicStateHash()>>32);
 return failure;
}
#endif
