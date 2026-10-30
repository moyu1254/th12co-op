#pragma once
#include <stdint.h>
#include <string.h>

// Q16 damage with an integer-ratio multiplier. Histories contain accepted, pre-coop damage;
// one common multiplier is sealed only after every target in the frame reports.
namespace coop_damage_budget {
constexpr uint64_t Unit=65536;
struct Ratio {uint64_t numerator=1,denominator=1;};
// Native EnemyData order: extended damage callback, actor recovery, the
// protected-actor spell restriction, then the spell's final division by 30.
static uint32_t NativeAmount(uint32_t damage,bool ring,bool recovery,bool protectedSpell,bool thirtieth){
 if(ring){
  if(damage>=30)damage=30+(damage-30)*3/10;
  if(damage>=70)damage=50+(damage-50)/5;
  if(damage>80)damage=80;
 }
 if(recovery)damage/=5;
 if(protectedSpell)damage/=5;
 if(thirtieth)damage/=30;
 return damage;
}
struct Window {
 uint64_t frames[60][2]{},sum[2]{};
 uint32_t cursor=0;
 void Clear(){memset(this,0,sizeof(*this));}
 void Begin(uint32_t length){
  cursor=(cursor+1)%length;
  for(int p=0;p<2;p++){sum[p]-=frames[cursor][p];frames[cursor][p]=0;}
 }
 void Add(uint64_t a,uint64_t b){frames[cursor][0]+=a;frames[cursor][1]+=b;sum[0]+=a;sum[1]+=b;}
 Ratio Factor(uint32_t numerator,uint32_t denominator,bool both)const{
  const uint64_t total=sum[0]+sum[1],largest=sum[0]>sum[1]?sum[0]:sum[1];
  if(!both||!total||largest*numerator>=total*denominator)return {};
  return {largest*numerator,total*denominator};
 }
};
static uint64_t MultiplyDivide(uint64_t value,Ratio ratio){
 if(ratio.numerator==ratio.denominator)return value;
 // Native x86 has no uint128. Long division avoids overflow and avoids
 // rounding 0.60 down before it is multiplied by a frame's damage.
 uint64_t result=0,rest=0;
 for(uint64_t bit=UINT64_C(1)<<63;bit;bit>>=1){
  rest=rest*2+((value&bit)?ratio.numerator:0);
  result=result*2+rest/ratio.denominator;rest%=ratio.denominator;
 }
 return result;
}
static uint32_t Apply(uint64_t shot,Ratio factor,uint32_t& remainder){
 // The fractional product is retained across frames for this target. No
 // category-wide remainder can be awarded to whichever enemy happens to run first.
 const uint64_t fixed=MultiplyDivide(shot,factor)+remainder;
 remainder=(uint32_t)(fixed%Unit);return (uint32_t)(fixed/Unit);
}
}
