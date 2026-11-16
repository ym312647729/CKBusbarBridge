import {DefinitionError} from './expressions.mjs';
import {inverseRigidMatrix,multiplyMatrix,transformPoint} from './geometry.mjs';

function baseSections(objects,results){
  return objects.filter(object=>object.baseBinding?.role==='base').map(object=>{
    const {envelope,port}=results.get(object.id)?.derivedGeometry??{};
    if(!envelope||!port)throw new DefinitionError('基座缺少已驱动的外壳边界：'+object.name);
    return {pose:object.transform,faces:[envelope.min[2],envelope.max[2]].map(side=>[port[0],envelope.max[1],side])};
  });
}

// Casing follows the two already installed cabinet bases. Only the visible
// shells vary between the end sections; copper, support and frame poses stay put.
export function facingRegularFrameCasings(objects,results){
  const bases=objects.filter(object=>object.baseBinding?.role==='base');
  if(bases.length!==2||new Set(bases.map(object=>object.baseBinding.positionName[0])).size!==2)
    throw new DefinitionError('常规基架外壳需要 A、B 两端各一个已安装基座');
  const sections=baseSections(bases,results);
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

// Other paths can contain corner, widened, and taller modules. Preserve those
// envelopes and their seal plates; only fit straight sections to actual mates.
export function attachedStraightFrameCasings(objects,results){
  const bases=baseSections(objects,results),casings=new Map();
  for(const object of objects.filter(row=>row.frameBinding)){
    const preview=results.get(object.id).derivedGeometry;
    if(!['Regular024','SideToSidePhaseChange029'].includes(preview.structureKind))continue;
    const {envelope,shapeToNative}=preview,intoFrame=inverseRigidMatrix(shapeToNative?
      multiplyMatrix(object.transform,shapeToNative):object.transform);
    const nominal={left:envelope.min[2],right:envelope.max[2],top:envelope.max[1]};
    const sections=[envelope.min[0],envelope.max[0]].map(station=>{
      const matches=bases.flatMap(base=>{
        const transform=multiplyMatrix(intoFrame,base.pose),[a,b]=base.faces.map(point=>transformPoint(point,transform));
        if(Math.abs(a[0]-station)>.01||Math.abs(b[0]-station)>.01||Math.abs(a[1]-b[1])>.01)return [];
        const left=Math.min(a[2],b[2]),right=Math.max(a[2],b[2]);
        if(right<=left||right<=nominal.left||left>=nominal.right)return [];
        return [{left,right,top:(a[1]+b[1])/2}];
      });
      if(matches.length>1)throw new DefinitionError('基架端面匹配到多个基座：'+object.name);
      return matches[0]??nominal;
    });
    if(sections.some(section=>section!==nominal))casings.set(object.id,{start:sections[0],end:sections[1]});
  }
  return casings;
}
