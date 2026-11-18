import {DefinitionError} from './expressions.mjs';
import {originalCopperSectionFrames} from './original-copper.mjs';
import {solidToMesh,inverseRigidMatrix} from './geometry.mjs';

const add=(a,b)=>a.map((v,i)=>v+b[i]),sub=(a,b)=>a.map((v,i)=>v-b[i]),scale=(a,s)=>a.map(v=>v*s),
  dot=(a,b)=>a.reduce((sum,v,i)=>sum+v*b[i],0),length=a=>Math.hypot(...a),unit=a=>scale(a,1/length(a)),
  cross=(a,b)=>[a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0]];
const finiteVector=value=>Array.isArray(value)&&value.length===3&&value.every(Number.isFinite);

export function planCopperPreviewHoles(object,pattern,supports,rootCount,bushingHoles=[]){
  if(!pattern||!(pattern.diameter>0)||!Number.isFinite(pattern.diameter))throw new DefinitionError('铜排预览缺少已读取的搭接开孔工艺');
  const frames=originalCopperSectionFrames(object.originalCopper),width=object.originalCopper.busBarWidth,thickness=object.originalCopper.busBarThickness,holes=[];
  const append=hole=>{
    if(!finiteVector(hole.center)||!finiteVector(hole.normal)||!finiteVector(hole.axis))throw new DefinitionError('铜排孔的原几何坐标无效');
    if(!holes.some(previous=>length(sub(previous.center,hole.center))<=1&&Math.abs(previous.diameter-hole.diameter)<=1))holes.push(hole);
  };
  const mappedEnds=new Set();
  for(const source of bushingHoles)for(const end of [0,1]){
    const frame=end?frames.at(-1):frames[0],delta=sub(source.center,frame.center),distance=dot(delta,frame.thickness),
      along=dot(delta,frame.direction),across=dot(delta,frame.width),endStation=(end?1:-1)*frame.length/2;
    if(Math.abs(dot(unit(source.normal),frame.thickness))<Math.cos(Math.PI/180)||
      Math.abs(Math.abs(distance)-(source.thickness+thickness)/2)>1||Math.abs(along-endStation)>width||
      Math.abs(along)>frame.length/2-source.diameter/2||Math.abs(across)>width/2-source.diameter/2)continue;
    append({...source,id:'wall-'+source.id,center:sub(source.center,scale(frame.thickness,distance)),normal:frame.thickness,axis:source.axis,slotLength:source.diameter});
    mappedEnds.add(end);
  }
  // Same library offsets and end anchors as DQOpenHoles.GetHoleNameInAssUseMatingFaceOpenHoleInfo.
  for(const end of [0,1]){
    if(mappedEnds.has(end))continue;
    const frame=end?frames.at(-1):frames[0],anchor=add(frame.center,scale(frame.direction,(end?1:-1)*(frame.length-width)/2));
    const nativeNormal=[frame.thickness[0],frame.thickness[2],-frame.thickness[1]],positive=nativeNormal.reduce((sum,v)=>sum+v,0)>0,
      axis=unit(cross(frame.width,scale(frame.thickness,positive?1:-1)));
    if(!object.originalCopper.isVerticalBend&&Math.max(...nativeNormal.map(Math.abs))<Math.cos(Math.PI/180))continue;
    for(const [index,entry] of String(pattern.location).split(';').filter(value=>value.trim()).entries()){
      const offsets=entry.split(',').map(Number);if(offsets.length!==2||!offsets.every(Number.isFinite))throw new DefinitionError('铜排开孔工艺的孔位无法解析');
      append({id:'end-'+end+'-'+index,center:add(add(anchor,scale(axis,offsets[0])),scale(frame.width,offsets[1])),normal:frame.thickness,axis,
        diameter:pattern.diameter,slotLength:pattern.style==='圆孔'?pattern.diameter:pattern.slotLength});
    }
  }
  // Project the original insulator mating-circle centre onto each corresponding copper face.
  for(const support of supports??[])for(const frame of frames){
    const normal=unit(support.normal);if(Math.abs(dot(normal,frame.thickness))<Math.cos(Math.PI/180))continue;
    const delta=sub(support.center,frame.center),distance=dot(delta,frame.thickness),along=dot(delta,frame.direction),across=dot(delta,frame.width);
    if(Math.abs(distance)>thickness*(rootCount*2-1)+50||Math.abs(along)>frame.length/2-6.5||Math.abs(across)>width/2-6.5)continue;
    append({id:'support-'+support.id+'-'+holes.length,center:sub(support.center,scale(frame.thickness,distance)),normal:frame.thickness,axis:frame.direction,diameter:13,slotLength:13});
  }
  return holes.map(hole=>({...hole,center:hole.center.map(Math.fround),normal:hole.normal.map(Math.fround),axis:hole.axis.map(Math.fround),diameter:Math.fround(hole.diameter),slotLength:Math.fround(hole.slotLength)}));
}

export function cutCopperPreviewHoles(kernel,result,object,settings){
  const holes=object.originalHolePlan??[];if(!holes.length)return result;
  const cutters=[];let cutter;
  try{
    for(const hole of holes){
      if(!(hole.slotLength>=hole.diameter&&hole.diameter>0))throw new DefinitionError('铜排孔径或腰孔长度无效');
      const depth=object.originalCopper.busBarThickness+2,straight=hole.slotLength-hole.diameter,parts=[];
      try{
        for(const offset of straight>0?[-straight/2,straight/2]:[0]){
          const raw=kernel.Manifold.cylinder(depth,hole.diameter/2,hole.diameter/2,settings.arcSegments??24);
          try{parts.push(raw.translate([offset,0,-depth/2]));}finally{raw.delete();}
        }
        if(straight>0){const raw=kernel.Manifold.cube([straight,hole.diameter,depth]);try{parts.push(raw.translate([-straight/2,-hole.diameter/2,-depth/2]));}finally{raw.delete();}}
        const local=kernel.Manifold.union(parts),across=cross(hole.normal,hole.axis);
        try{cutters.push(local.transform([...hole.axis,0,...across,0,...hole.normal,0,...hole.center,1]));}finally{local.delete();}
      }finally{parts.forEach(part=>part.delete());}
    }
    cutter=kernel.Manifold.union(cutters);
    for(let index=0;index<result.solids.length;index++){const solid=result.solids[index],cut=solid.subtract(cutter);result.solids[index]=cut;solid.delete();}
    // Cached local meshes must include the cut, rather than bypass it with the uncut source mesh.
    result.solidSources=result.solids.map((solid,index)=>{
      const source=result.solidSources?.[index];
      if(!source?.transform)return {path:object.templatePath,bodyIndex:index,componentChain:[],localMesh:solidToMesh(solid),
        shapeInputSignature:JSON.stringify([object.originalCopper,holes,index])};
      const local=solid.transform(inverseRigidMatrix(source.transform));
      try{return {...source,localMesh:solidToMesh(local),shapeInputSignature:JSON.stringify([object.originalCopper,holes,index])};}
      finally{local.delete();}
    });
    result.derivedGeometry={...result.derivedGeometry,holes:holes.length};return result;
  }finally{cutter?.delete();cutters.forEach(part=>part.delete());}
}
