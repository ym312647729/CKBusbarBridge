import {DefinitionError} from './expressions.mjs';

// Rectangular section follows the tangent without twist. As in the reference
// PreviewCore/OffsetLineSegment, flat bends use thickness and edge bends use width.
// Paths here explicitly describe the section centre, in millimetres.
const epsilon=1e-6;
const add=(a,b)=>a.map((v,i)=>v+b[i]),sub=(a,b)=>a.map((v,i)=>v-b[i]),scale=(a,s)=>a.map(v=>v*s);
const dot=(a,b)=>a.reduce((s,v,i)=>s+v*b[i],0),cross=(a,b)=>[a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0]];
const length=a=>Math.hypot(...a),unit=a=>scale(a,1/length(a));
function vector(value,label){if(!Array.isArray(value)||value.length!==3||!value.every(Number.isFinite))throw new DefinitionError(label+'必须包含三个有限坐标');return [...value];}
function positive(value,label){if(!Number.isFinite(value)||value<=0)throw new DefinitionError(label+'必须为有限正数');return value;}
function rotate(v,axis,angle){return add(add(scale(v,Math.cos(angle)),scale(cross(axis,v),Math.sin(angle))),scale(axis,dot(axis,v)*(1-Math.cos(angle))));}
export const freeCopperVersion='free-copper/1';
export function planFreeCopper(definition,overrides={}){
  if(definition?.version!==freeCopperVersion)throw new DefinitionError('自由铜排定义版本无效');
  for(const key of Object.keys(overrides))if(!['widthMm','thicknessMm','flatRadiusMm','edgeRadiusMm'].includes(key))throw new DefinitionError('自由铜排包含未知尺寸：'+key);
  const data={...structuredClone(definition),...overrides};
  for(const key of ['widthMm','thicknessMm','flatRadiusMm','edgeRadiusMm'])positive(data[key],key);
  if(data.thicknessMm>data.widthMm)throw new DefinitionError('铜排板厚不能大于排宽');
  if(!Array.isArray(data.points)||data.points.length<2||data.points.length>512)throw new DefinitionError('铜排路径需要2至512个点');
  const points=data.points.map((p,i)=>vector(p,'路径点'+(i+1))),directions=[],lengths=[];
  for(let i=0;i<points.length-1;i++){const delta=sub(points[i+1],points[i]),span=length(delta);if(span<=epsilon)throw new DefinitionError('相邻铜排路径点重合：'+(i+1));directions.push(unit(delta));lengths.push(span);}
  let width=vector(data.widthDirection,'起始排宽方向');if(length(width)<=epsilon||Math.abs(dot(unit(width),directions[0]))>epsilon)throw new DefinitionError('起始排宽方向必须垂直于首段路径');width=unit(width);
  let thickness=cross(directions[0],width);
  const frames=[{width,thickness}],corners=Array(points.length).fill(null);
  for(let i=1;i<points.length-1;i++){
    const before=directions[i-1],after=directions[i],angle=Math.acos(Math.max(-1,Math.min(1,dot(before,after))));
    if(angle>Math.PI/2+epsilon)throw new DefinitionError('铜排内角不能小于90°：路径点'+(i+1));
    if(angle>epsilon){
      const axis=unit(cross(before,after)),flat=Math.abs(dot(axis,width))>1-epsilon,edge=Math.abs(dot(axis,thickness))>1-epsilon;
      if(!flat&&!edge)throw new DefinitionError('折弯平面必须沿当前板厚或排宽方向，不能隐式扭转截面：路径点'+(i+1));
      const sectionSize=flat?data.thicknessMm:data.widthMm,radius=(flat?data.flatRadiusMm:data.edgeRadiusMm)+sectionSize/2,setback=radius*Math.tan(angle/2),entry=sub(points[i],scale(before,setback)),exit=add(points[i],scale(after,setback)),center=add(entry,scale(cross(axis,before),radius));
      corners[i]={index:i,kind:flat?'flat':'edge',axis,angle,internalAngleDegrees:180-angle*180/Math.PI,innerRadiusMm:radius-sectionSize/2,centerRadiusMm:radius,setback,entry,exit,center,width,thickness};
      width=rotate(width,axis,angle);thickness=rotate(thickness,axis,angle);
    }
    frames.push({width,thickness});
  }
  const segments=lengths.map((span,i)=>{
    const startTrim=corners[i]?.setback??0,endTrim=corners[i+1]?.setback??0;
    if(span-startTrim-endTrim<=epsilon)throw new DefinitionError('相邻折弯占用的直段长度不足：第'+(i+1)+'段');
    return {index:i,direction:directions[i],width:frames[i].width,thickness:frames[i].thickness,start:add(points[i],scale(directions[i],startTrim)),end:sub(points[i+1],scale(directions[i],endTrim)),startTrim,endTrim,originalLengthMm:span,lengthMm:span-startTrim-endTrim};
  });
  if(data.holes!==undefined&&!Array.isArray(data.holes))throw new DefinitionError('铜排孔位格式无效');
  const holeIds=new Set(),holes=(data.holes??[]).map((hole,i)=>{
    if(!hole?.id||holeIds.has(hole.id))throw new DefinitionError('铜排孔标识缺失或重复');holeIds.add(hole.id);
    if(!Number.isInteger(hole.segmentIndex)||!segments[hole.segmentIndex])throw new DefinitionError('铜排孔所属直段无效');
    const diameterMm=positive(hole.diameterMm,'孔径'),slotLengthMm=positive(hole.slotLengthMm??diameterMm,'孔长'),segment=segments[hole.segmentIndex];
    if(slotLengthMm<diameterMm)throw new DefinitionError('腰孔长度不能小于孔径');
    if(!Number.isFinite(hole.distanceMm)||!Number.isFinite(hole.widthOffsetMm))throw new DefinitionError('孔距及宽度偏移必须有限');
    if(hole.distanceMm-slotLengthMm/2<=segment.startTrim+epsilon||hole.distanceMm+slotLengthMm/2>=segment.originalLengthMm-segment.endTrim-epsilon)throw new DefinitionError('孔必须完整位于折弯外的直段内：孔'+(i+1));
    if(Math.abs(hole.widthOffsetMm)+diameterMm/2>=data.widthMm/2-epsilon)throw new DefinitionError('孔超出铜排宽度或与边界相切：孔'+(i+1));
    return {...hole,diameterMm,slotLengthMm,center:add(add(points[hole.segmentIndex],scale(segment.direction,hole.distanceMm)),scale(segment.width,hole.widthOffsetMm))};
  });
  const developedLengthMm=segments.reduce((s,p)=>s+p.lengthMm,0)+corners.filter(Boolean).reduce((s,p)=>s+p.centerRadiusMm*p.angle,0);
  for(let i=0;i<holes.length;i++)for(let j=0;j<i;j++)if(holes[i].segmentIndex===holes[j].segmentIndex){
    const first=holes[i],second=holes[j],straightSum=(first.slotLengthMm-first.diameterMm+second.slotLengthMm-second.diameterMm)/2,alongGap=Math.max(0,Math.abs(first.distanceMm-second.distanceMm)-straightSum);
    if(Math.hypot(alongGap,first.widthOffsetMm-second.widthOffsetMm)<=(first.diameterMm+second.diameterMm)/2+epsilon)throw new DefinitionError('铜排孔之间重叠或相切，请调整孔位');
  }
  return {data:{...data,points,widthDirection:unit(data.widthDirection),holes:data.holes??[]},segments,corners:corners.filter(Boolean),holes,developedLengthMm};
}
export function validateFreeCopper(definition,overrides={}){return planFreeCopper(definition,overrides).data;}
export function createFreeCopperObject(definition,name='自由铜排',phase=null){
  const freeCopper=validateFreeCopper(definition);
  return {id:crypto.randomUUID(),kind:'free-copper',name,category:'copper',parentId:null,templatePath:'free-draw/busbar',configuration:'自由绘制',sourceSha256:null,parameters:{},suppression:{},transform:[1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1],visible:true,phase,freeCopper};
}
function ring(center,width,thickness,data){return [[-1,-1],[1,-1],[1,1],[-1,1]].map(([w,t])=>add(add(center,scale(width,w*data.widthMm/2)),scale(thickness,t*data.thicknessMm/2)));}
function ringsToSolid(kernel,rings){
  const vertices=rings.flat(2),triangles=[0,2,1,0,3,2],last=(rings.length-1)*4;
  for(let i=0;i<rings.length-1;i++)for(let j=0;j<4;j++){const a=i*4+j,b=i*4+(j+1)%4,c=b+4,d=a+4;triangles.push(a,b,c,a,c,d);}
  triangles.push(last,last+1,last+2,last,last+2,last+3);
  const solid=new kernel.Manifold(new kernel.Mesh({numProp:3,vertProperties:new Float32Array(vertices),triVerts:new Uint32Array(triangles)}));
  if(solid.status()!=='NoError'||solid.volume()<=0){solid.delete();throw new DefinitionError('铜排折弯实体拓扑无效');}return solid;
}
function cutterForHole(kernel,hole,segment,thicknessMm,arcSegments){
  const depth=thicknessMm+2,straight=hole.slotLengthMm-hole.diameterMm,parts=[];
  try{
    for(const x of straight>epsilon?[-straight/2,straight/2]:[0]){const raw=kernel.Manifold.cylinder(depth,hole.diameterMm/2,hole.diameterMm/2,arcSegments),shifted=raw.translate([x,0,-depth/2]);raw.delete();parts.push(shifted);}
    if(straight>epsilon){const raw=kernel.Manifold.cube([straight,hole.diameterMm,depth]),shifted=raw.translate([-straight/2,-hole.diameterMm/2,-depth/2]);raw.delete();parts.push(shifted);}
    const local=kernel.Manifold.union(parts),matrix=[...segment.direction,0,...segment.width,0,...segment.thickness,0,...hole.center,1];
    try{return local.transform(matrix);}finally{local.delete();}
  }finally{parts.forEach(part=>part.delete());}
}
export function evaluateFreeCopper(kernel,object,settings={}){
  const started=performance.now(),plan=planFreeCopper(object.freeCopper,object.parameters),parts=[],cutters=[];let combined;
  const arcSegments=settings.arcSegments??24;if(!Number.isInteger(arcSegments)||arcSegments<12||arcSegments>720)throw new DefinitionError('铜排圆弧分段无效');
  try{
    for(const segment of plan.segments)parts.push(ringsToSolid(kernel,[ring(segment.start,segment.width,segment.thickness,plan.data),ring(segment.end,segment.width,segment.thickness,plan.data)]));
    let maximumChordErrorMm=0;
    for(const corner of plan.corners){
      const count=Math.max(1,Math.ceil(corner.angle*arcSegments/(2*Math.PI)-1e-10)),radial=sub(corner.entry,corner.center),rings=[];
      const outerRadius=2*corner.centerRadiusMm-corner.innerRadiusMm;maximumChordErrorMm=Math.max(maximumChordErrorMm,outerRadius*(1-Math.cos(corner.angle/count/2)));
      for(let i=0;i<=count;i++){const angle=corner.angle*i/count,center=i===0?corner.entry:i===count?corner.exit:add(corner.center,rotate(radial,corner.axis,angle));rings.push(ring(center,rotate(corner.width,corner.axis,angle),rotate(corner.thickness,corner.axis,angle),plan.data));}
      parts.push(ringsToSolid(kernel,rings));
    }
    combined=kernel.Manifold.union(parts);
    if(combined.status()!=='NoError'||combined.isEmpty())throw new DefinitionError('铜排各直段和折弯不能形成有效实体');
    const sum=parts.reduce((s,p)=>s+p.volume(),0),volumeTolerance=Math.max(1e-3,sum*2e-6);
    if(sum-combined.volume()>volumeTolerance)throw new DefinitionError('铜排路径自身相交或折弯材料重叠');
    const constructionMs=performance.now()-started,cutStarted=performance.now();
    for(const hole of plan.holes){
      const cutter=cutterForHole(kernel,hole,plan.segments[hole.segmentIndex],plan.data.thicknessMm,arcSegments);cutters.push(cutter);
      const target=parts[hole.segmentIndex].intersect(cutter),all=combined.intersect(cutter);
      try{if(all.volume()-target.volume()>Math.max(1e-4,target.volume()*1e-5))throw new DefinitionError('孔轴切到其他排段，请调整孔位或路径');}finally{target.delete();all.delete();}
      for(const previous of cutters.slice(0,-1)){const common=previous.intersect(cutter);try{if(common.volume()>epsilon)throw new DefinitionError('铜排孔之间重叠，请合并或调整孔位');}finally{common.delete();}}
      const cut=combined.subtract(cutter);combined.delete();combined=cut;
      if(combined.status()!=='NoError'||combined.isEmpty())throw new DefinitionError('铜排开孔失败');
    }
    const solid=combined;combined=null;
    const sectionEdges=plan.segments.flatMap(segment=>[segment.start,segment.end].map(center=>ring(center,segment.width,segment.thickness,plan.data)));
    const parameters=[['widthMm','排宽'],['thicknessMm','板厚'],['flatRadiusMm','平弯内半径'],['edgeRadiusMm','立弯内半径']].map(([name,label])=>({name,label,value:plan.data[name],editable:true,annotationEdges:name==='widthMm'?sectionEdges.flatMap(r=>[[r[0],r[1]],[r[2],r[3]]]):name==='thicknessMm'?sectionEdges.flatMap(r=>[[r[1],r[2]],[r[3],r[0]]]):[]}));
    return {solids:[solid],parameters,parameterMs:0,geometryTimings:{otherFeatureMs:constructionMs,cutFeatureMs:performance.now()-cutStarted},preview:{complexFeatureCount:0,canSimplify:false,coarsened:false,arcSegments,maximumChordErrorMm},derivedGeometry:{kind:'free-copper',version:freeCopperVersion,pathConvention:'矩形截面中心线，毫米；平弯沿板厚，立弯沿排宽；不隐式投影或扭转路径',developedLengthMm:plan.developedLengthMm,bends:plan.corners.map(c=>({pathPointIndex:c.index,kind:c.kind,innerRadiusMm:c.innerRadiusMm,internalAngleDegrees:c.internalAngleDegrees})),holes:plan.holes.length,analyticVolumeWithoutHolesMm3:plan.developedLengthMm*plan.data.widthMm*plan.data.thicknessMm,discreteVolumeMm3:solid.volume(),maximumChordErrorMm}};
  }finally{combined?.delete();parts.forEach(part=>part.delete());cutters.forEach(part=>part.delete());}
}
