import {DefinitionError} from './expressions.mjs';
import {inverseRigidMatrix,multiplyMatrix,transformPoint} from './geometry.mjs';

// Casing follows the two already installed cabinet bases. Only the visible
// shells vary between the end sections; copper, support and frame poses stay put.
export function facingRegularFrameCasings(objects,results){
  const bases=objects.filter(object=>object.baseBinding?.role==='base');
  if(bases.length!==2||new Set(bases.map(object=>object.baseBinding.positionName[0])).size!==2)
    throw new DefinitionError('常规基架外壳需要 A、B 两端各一个已安装基座');
  const sections=bases.map(object=>{
    const {envelope,port}=results.get(object.id)?.derivedGeometry??{};
    if(!envelope||!port)throw new DefinitionError('基座缺少已驱动的外壳边界：'+object.name);
    return {pose:object.transform,faces:[envelope.min[2],envelope.max[2]].map(side=>[port[0],envelope.max[1],side])};
  });
  const casings=new Map();
  for(const object of objects.filter(object=>object.frameBinding)){
    const {envelope}=results.get(object.id)?.derivedGeometry??{};
    if(!envelope)throw new DefinitionError('常规基架缺少已驱动的接口端面：'+object.name);
    const intoFrame=inverseRigidMatrix(object.transform);
    const ends=sections.map(section=>{
      const mapping=multiplyMatrix(intoFrame,section.pose),[first,second]=section.faces.map(point=>transformPoint(point,mapping));
      if(Math.abs(first[0]-second[0])>1e-5||Math.abs(first[1]-second[1])>1e-5)
        throw new DefinitionError('常规基架与基座的侧板方向不一致：'+object.name);
      return {station:first[0],top:first[1],left:Math.min(first[2],second[2]),right:Math.max(first[2],second[2])};
    }).sort((a,b)=>a.station-b.station);
    const [first,last]=ends,span=last.station-first.station;
    if(span<=0||envelope.min[0]<first.station-.01||envelope.max[0]>last.station+.01)
      throw new DefinitionError('常规基架端面不在两个基座接口之间：'+object.name);
    const sectionAt=station=>{
      const ratio=Math.max(0,Math.min(1,(station-first.station)/span));
      return Object.fromEntries(['top','left','right'].map(key=>[key,first[key]+(last[key]-first[key])*ratio]));
    };
    casings.set(object.id,{start:sectionAt(envelope.min[0]),end:sectionAt(envelope.max[0])});
  }
  return casings;
}
