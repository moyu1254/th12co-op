#include "coop_damage_budget.h"
#include <assert.h>
#include <stdio.h>
#include <initializer_list>
using namespace coop_damage_budget;
int main(){
 assert(NativeAmount(15,false,false,false,true)==0);
 assert(NativeAmount(15,false,false,false,true)*2==0); // Do not divide 15+15 after combining.
 assert(NativeAmount(124,true,true,false,false)==11); // F(124)/5, not sum(F(4/5)).
 assert(NativeAmount(465,true,false,false,true)==2);
 assert(NativeAmount(80,false,false,true,false)==16);
 assert(NativeAmount(60,false,false,true,false)==12); // Mixed bomb baseline remains 12.
 for(auto input: {100,50,20,0}){
  Window w;w.Begin(60);w.Add(100*Unit,input*Unit);uint32_t remainder=0;
  assert(Apply((100+input)*Unit,w.Factor(6,5,true),remainder)==(input?120u:100u));
 }
 Window boss,stage,ufo;boss.Add(100*Unit,100*Unit);stage.Add(100*Unit,100*Unit);ufo.Add(100*Unit,100*Unit);
 uint32_t r=0;assert(Apply(200*Unit,boss.Factor(6,5,true),r)==120);
 r=0;assert(Apply(200*Unit,stage.Factor(5,4,true),r)==125);
 r=0;assert(Apply(200*Unit,ufo.Factor(1,1,true),r)==100);
 // Actor symmetry, split targets and repeated one-point hits retain fractions.
 for(uint32_t a=0;a<120;a+=7)for(uint32_t b=0;b<120;b+=11){
  Window x,y;x.Add(a*Unit,b*Unit);y.Add(b*Unit,a*Unit);
  auto f=x.Factor(6,5,true),g=y.Factor(6,5,true);uint32_t rx=0,ry=0;
  assert(Apply((a+b)*Unit,f,rx)==Apply((a+b)*Unit,g,ry));
 }
 r=0;uint32_t damage=0;for(int n=0;n<1000;n++)damage+=Apply(Unit,{3,5},r);
 assert(damage>=599&&damage<=600&&r<Unit);
 uint32_t first=0,second=0,firstReverse=0,secondReverse=0;
 for(int n=0;n<300;n++){
  auto a=Apply(7*Unit,{3,5},first),b=Apply(11*Unit,{3,5},second);
  auto br=Apply(11*Unit,{3,5},secondReverse),ar=Apply(7*Unit,{3,5},firstReverse);
  assert(a==ar&&b==br);
 }
 // History expires at the exact gameplay-frame horizon, including no-hit frames.
 for(uint32_t length: {30u,60u}){
  Window w;w.Begin(length);w.Add(100*Unit,100*Unit);
  for(uint32_t n=1;n<length;n++){w.Begin(length);assert(w.sum[1]==100*Unit);}
  w.Begin(length);assert(w.sum[0]==0&&w.sum[1]==0);
  assert(w.Factor(6,5,true).numerator==w.Factor(6,5,true).denominator);
 }
 assert(boss.Factor(6,5,false).numerator==boss.Factor(6,5,false).denominator);
 boss.Clear();assert(stage.sum[0]==100*Unit&&ufo.sum[1]==100*Unit);
 // Product exceeds uint64, quotient remains exact on the x86 build.
 assert(MultiplyDivide(UINT64_C(1)<<40,{UINT64_C(1)<<40,UINT64_C(1)<<41})==(UINT64_C(1)<<39));
 puts("damage budget: examples, categories, symmetry, distributed hits, expiry, incapacitation, fractions, overflow: PASS");
}
