import {DefinitionError} from './expressions.mjs';
const near=(left,right)=>Math.hypot(left[0]-right[0],left[1]-right[1])<1e-6;
const cross=(left,right)=>left[0]*right[1]-left[1]*right[0];
const minus=(left,right)=>left.map((value,index)=>value-right[index]);
const addScaled=(point,vector,distance)=>point.map((value,index)=>value+vector[index]*distance);
const normalized=vector=>{const length=Math.hypot(...vector);if(length<1e-8)throw new DefinitionError('开口钣金存在零长度段');return vector.map(value=>value/length);};

export function openSheetBoundary(sketch,properties,arcSegments,orientationWitnesses=[]){
  const sourceSegments=sketch.segments.filter(segment=>!segment.construction);
  const contours=sketch.contours?.filter(contour=>!contour.closed);
  if(contours?.length!==1||contours[0].segments?.length!==sourceSegments.length)throw new DefinitionError('开口钣金缺少唯一原生轮廓顺序');
  const segments=contours[0].segments.map(identity=>{
    const matching=sourceSegments.filter(segment=>segment.id===identity);
    if(matching.length!==1)throw new DefinitionError('开口钣金原生轮廓无法唯一对应草图段');
    return matching[0];
  });
  if(!segments.length||segments.length>2||segments.some(segment=>segment.type!==0))throw new DefinitionError('开口钣金的多段或圆弧路径尚未支持');
  const sourceLines=segments.map(segment=>({start:segment.geometry.start.slice(0,2).map(value=>value*1000),end:segment.geometry.end.slice(0,2).map(value=>value*1000)}));
  let first=sourceLines[0].start;
  if(sourceLines.length===2){
    const firstStartShared=[sourceLines[1].start,sourceLines[1].end].some(point=>near(point,first));
    if(firstStartShared)first=sourceLines[0].end;
  }
  const path=[first],remaining=[...sourceLines];
  while(remaining.length){
    const candidates=remaining.flatMap((line,index)=>near(line.start,path.at(-1))?[{index,next:line.end}]:near(line.end,path.at(-1))?[{index,next:line.start}]:[]);
    if(candidates.length!==1)throw new DefinitionError('开口钣金路径未能按原段唯一连接');
    const selected=candidates[0];remaining.splice(selected.index,1);path.push(selected.next);
  }
  const thickness=properties.Thickness*1000;
  if(!(thickness>0))throw new DefinitionError('开口钣金厚度缺失');
  const signedThickness=properties.ReverseThickness?thickness:-thickness;
  const offsets=properties.SymmetricThickness?[-thickness/2,thickness/2]:[Math.min(0,signedThickness),Math.max(0,signedThickness)];
  function boundaryForPath(path){
  const directions=path.slice(1).map((point,index)=>normalized(minus(point,path[index]))),normals=directions.map(direction=>[-direction[1],direction[0]]);
  if(path.length===2){const chains=offsets.map(offset=>path.map(point=>addScaled(point,normals[0],offset)));return [...chains[0],...chains[1].reverse()];}
  const turn=Math.atan2(cross(...directions),directions[0][0]*directions[1][0]+directions[0][1]*directions[1][1]);
  if(Math.abs(Math.abs(turn)-Math.PI/2)>1e-8)throw new DefinitionError('开口钣金的非直角折弯尚未支持');
  const insideRadius=properties.BendRadius*1000;
  if(!(insideRadius>0))throw new DefinitionError('开口钣金折弯半径缺失');
  const turnSign=Math.sign(turn),innerOffset=turnSign*Math.max(...offsets.map(offset=>turnSign*offset)),centerOffset=innerOffset+turnSign*insideRadius;
  const firstOffset=addScaled(path[1],normals[0],centerOffset),secondOffset=addScaled(path[1],normals[1],centerOffset);
  const center=addScaled(firstOffset,directions[0],cross(minus(secondOffset,firstOffset),directions[1])/cross(...directions));
  const steps=Math.max(6,Math.ceil(Math.abs(turn)*arcSegments/(2*Math.PI)));
  const chains=offsets.map(offset=>{
    const radius=insideRadius+turnSign*(innerOffset-offset);
    const tangentStart=addScaled(center,normals[0],-turnSign*radius),tangentEnd=addScaled(center,normals[1],-turnSign*radius);
    for(const [tangent,index] of [[tangentStart,0],[tangentEnd,1]]){
      const projection=minus(tangent,addScaled(path[index],normals[index],offset)).reduce((sum,value,axis)=>sum+value*directions[index][axis],0);
      if(projection<-1e-5||projection>Math.hypot(...minus(path[index+1],path[index]))+1e-5)throw new DefinitionError('开口钣金半径超过原始直线可用长度');
    }
    const startAngle=Math.atan2(tangentStart[1]-center[1],tangentStart[0]-center[0]);
    const arc=Array.from({length:steps+1},(_,index)=>[center[0]+radius*Math.cos(startAngle+turn*index/steps),center[1]+radius*Math.sin(startAngle+turn*index/steps)]);
    return [addScaled(path[0],normals[0],offset),...arc,addScaled(path.at(-1),normals[1],offset)];
  });
  return [...chains[0],...chains[1].reverse()];
  }
  if(path.length===2||properties.SymmetricThickness)return boundaryForPath(path);
  // GetSketchSegments and contour arrays have no common traversal direction. A native
  // sketch attached directly to a straight sheet face fixes the material side uniquely.
  if(!orientationWitnesses.length)throw new DefinitionError('开口折弯缺少原生表面草图，无法唯一核验厚度侧');
  const onBoundary=(point,boundary)=>boundary.some((start,index)=>{
    const end=boundary[(index+1)%boundary.length],direction=minus(end,start),delta=minus(point,start),length=Math.hypot(...direction);
    if(length<1e-8)return near(point,start);
    const projection=delta.reduce((sum,value,axis)=>sum+value*direction[axis],0)/length;
    return Math.abs(cross(direction,delta))/length<1e-5&&projection>=-1e-5&&projection<=length+1e-5;
  });
  const candidates=[path,[...path].reverse()].map(boundaryForPath).filter(boundary=>orientationWitnesses.every(point=>onBoundary(point,boundary)));
  if(candidates.length!==1)throw new DefinitionError('原生表面草图未能唯一核验开口折弯厚度侧');
  return candidates[0];
}
