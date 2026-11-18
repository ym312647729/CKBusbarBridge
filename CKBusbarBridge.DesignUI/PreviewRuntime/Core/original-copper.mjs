import {DefinitionError} from './expressions.mjs';
import {freeCopperVersion,planFreeCopper,evaluateFreeCopper,createFreeCopperObject} from './free-copper.mjs';
import {solidToMesh} from './geometry.mjs';
const add=(first,second)=>first.map((value,axis)=>value+second[axis]),sub=(first,second)=>first.map((value,axis)=>value-second[axis]),scale=(vector,factor)=>vector.map(value=>value*factor);
const dot=(first,second)=>first.reduce((sum,value,axis)=>sum+value*second[axis],0),cross=(first,second)=>[first[1]*second[2]-first[2]*second[1],first[2]*second[0]-first[0]*second[2],first[0]*second[1]-first[1]*second[0]],norm=vector=>Math.hypot(...vector),unit=vector=>scale(vector,1/norm(vector));
const vector=(value,label)=>{if(!Array.isArray(value)||value.length!==3||!value.every(Number.isFinite))throw new DefinitionError(label+'必须包含三个有限坐标');return [...value];};
const positive=(value,label)=>{if(!Number.isFinite(value)||value<=0)throw new DefinitionError(label+'必须为有限正数');return value;};
export const originalCopperVersion='original-planar-bend-copper/1';

// Direct mathematical port of PlanarBendBusbarSetting. The original PointList
// is a drawing reference line. IsCenter=true previews its -thickness/2 offset;
// it is not interchangeable with the free-drawing section centre convention.
export function offsetOriginalCopperPath(setting,offsetDistance){
  const points=setting.pointList.map(point=>vector(point,'原铜排路径点')),width=unit(vector(setting.widthVector,'原排宽方向')),start=unit(vector(setting.startBusbarVector,'原板厚方向'));
  if(!Number.isFinite(offsetDistance))throw new DefinitionError('原铜排平行偏移量必须有限');
  if(Math.abs(offsetDistance)<1e-9)return points;
  const segments=[];let orientation;
  for(let index=1;index<points.length;index++){
    const delta=sub(points[index],points[index-1]);if(norm(delta)<1e-8)continue;
    const direction=unit(delta),normal=cross(direction,width);if(norm(normal)<1e-8)throw new DefinitionError('原铜排排宽方向与路径平行');
    const thickness=unit(normal);orientation??=dot(thickness,start)<0?-1:1;
    const translation=scale(thickness,orientation*offsetDistance);segments.push({start:add(points[index-1],translation),end:add(points[index],translation),direction});
  }
  if(!segments.length)throw new DefinitionError('原铜排没有非零路径段');
  const output=[segments[0].start];
  for(let index=1;index<segments.length;index++){
    const first=segments[index-1],second=segments[index],denominator=dot(width,cross(first.direction,second.direction));
    output.push(Math.abs(denominator)<1e-8?scale(add(first.end,second.start),.5):add(first.start,scale(first.direction,dot(width,cross(sub(second.start,first.start),second.direction))/denominator)));
  }
  output.push(segments.at(-1).end);return output;
}

export function offsetOriginalCopperPreservingPoints(setting,offsetDistance){
  const points=[],indices=[];
  for(const point of setting.pointList){if(!points.length||norm(sub(point,points.at(-1)))>.001)points.push([...point]);indices.push(points.length-1);}
  const moved=offsetOriginalCopperPath({...setting,pointList:points},offsetDistance);
  if(moved.length!==points.length)throw new DefinitionError('原叠排平行偏移未保持原路径结构');return indices.map(index=>[...moved[index]]);
}

export function originalCopperDevelopedLength(setting){return setting.pointList.slice(1).reduce((sum,point,index)=>sum+norm(sub(point,setting.pointList[index])),0)+Math.max(0,setting.pointList.length-2)*Math.max(0,setting.bendAllowance);}

export function splitOriginalCopperPieces(pieces,accepts=()=>true){
  const result=structuredClone(pieces);
  for(const piece of result){
    if(!Array.isArray(piece.pointList)||piece.pointList.length<2)throw new DefinitionError('原铜排自动分段需要完整路径');piece.pointList.forEach(point=>vector(point,'原分段坐标'));positive(piece.busBarWidth,'原分段排宽');positive(piece.busBarThickness,'原分段板厚');
    if(!Number.isFinite(piece.bendAllowance)||piece.bendAllowance<0)throw new DefinitionError('原折弯补偿必须为有限非负数');
  }
  for(let index=0;index<result.length;index++){
    if(result.length>10000)throw new DefinitionError('原铜排分段数量超出可预览范围');
    const source=result[index],originalLength=originalCopperDevelopedLength(source);if(originalLength<=3000.001)continue;
    const candidates=[];let prefix=0;
    if(!source.isMilledBar)for(let leg=0;leg<source.pointList.length-1;leg++){
      const travel=sub(source.pointList[leg+1],source.pointList[leg]),length=norm(travel),direction=unit(travel),width=unit(source.widthVector),reserve=source.busBarWidth+40+50+Math.max(source.barBendingRadius,source.verticalBendingRadius??0);
      if(length>.001&&norm(source.widthVector)>.001&&Math.abs(direction[1])<=.001&&Math.abs(width[1])<=.001&&Math.abs(dot(direction,width))<=.001){
        const low=reserve,high=Math.min(length-reserve,3000-prefix-leg*Math.max(0,source.bendAllowance)-source.busBarWidth/2);
        if(high>=low)for(const distance of new Set([Math.max(low,Math.min(high,2000-prefix)),low,high]))candidates.push({leg,distance,deviation:Math.abs(prefix+distance-2000)});
      }
      prefix+=length;
    }
    candidates.sort((first,second)=>first.deviation-second.deviation||first.leg-second.leg||first.distance-second.distance);let selected;
    for(const {leg,distance}of candidates){
      const direction=unit(sub(source.pointList[leg+1],source.pointList[leg])),cut=add(source.pointList[leg],scale(direction,distance)),halfLap=source.busBarWidth/2,first={...structuredClone(source),groupKey:source.groupKey+'/split',endLapWidth:source.busBarWidth,pointList:[...source.pointList.slice(0,leg+1).map(point=>[...point]),add(cut,scale(direction,halfLap))]},bridge=originalBridgeReferencePoints({start:add(cut,scale(direction,-halfLap)),end:source.pointList[leg+1],lengthDirection:direction,offsetDirection:[0,1,0],widthMm:source.busBarWidth,thicknessMm:source.busBarThickness,startOffsetMm:source.busBarThickness,endOffsetMm:0}),tail={...structuredClone(source),groupKey:source.groupKey+'/tail',startBusbarVector:[0,1,0],startLapWidth:source.busBarWidth,pointList:[...bridge.slice(0,3),...source.pointList.slice(leg+1).map(point=>[...point])]};
      for(const piece of [first,tail])piece.previewBendPointKinds=piece.pointList.map(point=>{const original=source.pointList.findIndex(candidate=>norm(sub(candidate,point))<.001);return original<0?0:source.previewBendPointKinds?.length===source.pointList.length?source.previewBendPointKinds[original]:source.isVerticalBend?1:0;});
      if(originalCopperDevelopedLength(first)>3000.001||originalCopperDevelopedLength(tail)>=originalLength-.001)continue;
      const proposed=[...result.slice(0,index),first,tail,...result.slice(index+1)];if(!accepts(proposed))continue;selected={first,tail};break;
    }
    if(!selected)throw new DefinitionError('铜排超长且找不到避开折弯或铣排并满足空间及净距的原直段断点：'+source.groupKey);
    result.splice(index,1,selected.first,selected.tail);
  }
  return result;
}

export function originalBridgeReferencePoints({start,end,lengthDirection,offsetDirection,widthMm,thicknessMm,startOffsetMm=thicknessMm,endOffsetMm=thicknessMm}){
  vector(start,'原桥头起点');vector(end,'原桥头终点');positive(widthMm,'原桥头排宽');positive(thicknessMm,'原桥头板厚');
  const length=unit(vector(lengthDirection,'原桥头长度方向')),offset=unit(vector(offsetDirection,'原桥头偏移方向'));
  if(![startOffsetMm,endOffsetMm,...length,...offset].every(Number.isFinite)||Math.abs(dot(length,offset))>1e-8)throw new DefinitionError('原桥头方向或偏移值无效');
  const first=add(start,scale(offset,startOffsetMm)),last=add(end,scale(offset,endOffsetMm)),straight=widthMm+40,transition=straight+50;
  return [first,add(first,scale(length,straight)),add(add(first,scale(length,transition)),scale(offset,-startOffsetMm)),add(add(last,scale(length,-transition)),scale(offset,-endOffsetMm)),add(last,scale(length,-straight)),last];
}

function interfaceContexts(interfaces,widthMm,thicknessMm){
  if(!Array.isArray(interfaces)||interfaces.length!==3||interfaces.some(points=>!Array.isArray(points)||!points.length||points.length>4))throw new DefinitionError('原固定接口需要完整三相、每相1至4根铜排');
  return interfaces.map(points=>{
    points.forEach(point=>vector(point,'原固定接口坐标'));const center=scale(points.reduce((sum,point)=>add(sum,point),[0,0,0]),1/points.length);
    if(points.some(point=>Math.abs(point[0]-center[0])>.2||Math.abs(point[2]-center[2])>.2))throw new DefinitionError('固定接口同相成员必须位于同一端面及横向位置');
    return {center,widthMm,thicknessMm,offsets:points.map(point=>point[1]-center[1]).sort((first,second)=>first-second)};
  });
}

// FaceToFaceModelBusbarPlanner.FixedInterfaces: ordinary pieces have a front
// bridge only. Stack offsets intersect offset lines; translating the entire
// piece would accumulate elevation at successive lap joints.
export function planOriginalOrdinaryCopper({path,bInterfaces,aInterfaces,widthMm,thicknessMm}){
  positive(widthMm,'原铜排排宽');positive(thicknessMm,'原铜排板厚');
  if(!path?.segments?.length||path.segments.some(segment=>segment.role!==0))throw new DefinitionError('此原铜排入口仅接受普通段，换相及偏移必须调用对应原工艺规划');
  const left=interfaceContexts(bInterfaces,widthMm,thicknessMm),right=interfaceContexts(aInterfaces,widthMm,thicknessMm),radius=Math.max(10,thicknessMm),pieces=[];
  for(let phase=0;phase<3;phase++){
    if(left[phase].offsets.length!==right[phase].offsets.length||left[phase].offsets.some((offset,index)=>Math.abs(offset-right[phase].offsets[index])>.2))throw new DefinitionError('两端同相叠排成员的板厚方向偏移不一致');
    if(Math.abs(left[phase].center[1]-right[phase].center[1])>.2)throw new DefinitionError('两端高度不同，不规划竖向过渡');
  }
  for(const [segmentIndex,segment]of path.segments.entries())for(let phase=0;phase<3;phase++){
    const context=segmentIndex===0?left[phase]:right[phase],start=[segment.start.x-widthMm/2,context.center[1],segment.start.y+context.center[2]-(segmentIndex===0?path.points[0].y:path.points.at(-1).y)],end=[segment.end.x+widthMm/2,right[phase].center[1],segment.end.y+right[phase].center[2]-path.points.at(-1).y];
    if(Math.abs(start[2]-end[2])>.2)throw new DefinitionError('普通段相位接口横移，必须规划换相或偏移工艺');
    if(end[0]-start[0]<=widthMm+40+50+radius)throw new DefinitionError('中间排长度不足以放置原前桥头和出口直段');
    const bridge=originalBridgeReferencePoints({start,end,lengthDirection:[1,0,0],offsetDirection:[0,1,0],widthMm,thicknessMm,startOffsetMm:thicknessMm,endOffsetMm:0}),innerOffset=Math.min(...context.offsets),reference={version:originalCopperVersion,isCenter:true,startBusbarVector:[0,1,0],widthVector:[0,0,1],pointList:[...bridge.slice(0,3),bridge.at(-1)].map(point=>add(point,[0,innerOffset,0])),busBarWidth:widthMm,busBarThickness:thicknessMm,barBendingRadius:radius,verticalBendingRadius:0,bendAllowance:20,startLapWidth:widthMm,endLapWidth:widthMm,planningSegmentIndex:segmentIndex,isVerticalBend:false,isMilledBar:false};
    for(const [memberIndex,offset]of context.offsets.entries()){
      const member={...structuredClone(reference),pointList:offsetOriginalCopperPreservingPoints(reference,offset-innerOffset),groupKey:'ABC'[phase]+'-'+(memberIndex+1)};
      if(member.pointList.slice(1).some((point,index)=>point[0]-member.pointList[index][0]<=.001))throw new DefinitionError('原前桥头叠排平行偏移后反向或重合');
      member.previewBendPointKinds=member.pointList.map(()=>0);pieces.push(member);
    }
  }
  return {version:'original-fixed-interface-ordinary-copper/1',pieces,source:'FaceToFaceModelBusbarPlanner.CreateFixedOrdinarySegment/CreateFixedFrontBridgeGroup',requiresPhysicalSubdivision:pieces.some(piece=>originalCopperDevelopedLength(piece)>3000.001)};
}

// Original coordinates are [length,height,width], platform coordinates are
// [length,width,height]. Both the drawing line and its original preview offset
// remain in the JSON; only this adapter converts into the shared mesh kernel.
export function originalCopperPreviewDefinition(setting){
  if(setting.version!==originalCopperVersion||setting.isMilledBar||setting.isVerticalBend)throw new DefinitionError('原平弯预览入口不接受铣排或立弯混合工艺');
  if(typeof setting.isCenter!=='boolean'||!Number.isFinite(setting.bendAllowance)||setting.bendAllowance<0)throw new DefinitionError('原铜排参考线或折弯补偿无效');
  for(const [name,label]of [['widthVector','原排宽方向'],['startBusbarVector','原板厚方向']])if(norm(vector(setting[name],label))<1e-8)throw new DefinitionError(label+'不能为零');
  positive(setting.busBarWidth,'原铜排排宽');positive(setting.busBarThickness,'原铜排板厚');positive(setting.barBendingRadius,'原平弯半径');
  if(!Array.isArray(setting.pointList)||setting.pointList.length<2||setting.pointList.some(point=>!Array.isArray(point)||point.length!==3||!point.every(Number.isFinite)))throw new DefinitionError('原平弯路径点无效');
  // 原生建模器以单侧截面放置材料；共享网格按截面中心向两侧扫掠。
  // 已保存的独立参考线绘制继续按原口径解释。
  const nativeMaterial=setting.pathContract==='native-material';
  const preview=(nativeMaterial?!setting.isCenter:setting.isCenter)
    ?offsetOriginalCopperPath(setting,(nativeMaterial?1:-1)*setting.busBarThickness/2):setting.pointList;
  const map=point=>[point[0],(setting.coordinateFrame==='solidworks'?-1:1)*point[2],point[1]],definition={version:freeCopperVersion,points:preview.map(map),widthDirection:map(setting.widthVector),widthMm:setting.busBarWidth,thicknessMm:setting.busBarThickness,flatRadiusMm:setting.barBendingRadius,edgeRadiusMm:setting.verticalBendingRadius>0?setting.verticalBendingRadius:setting.barBendingRadius,holes:[]};
  planFreeCopper(definition);return definition;
}

export function originalCopperFromDrawing(definition){
  // The drawing editor uses the platform axes. Preserve its points as the
  // original reference line, before the original half-thickness preview shift.
  const plan=planFreeCopper(definition);if(plan.corners.some(corner=>corner.kind!=='flat'))throw new DefinitionError('原参考线绘制当前接受平弯；含立弯的路径请选择截面中心线绘制');
  const drawing=plan.data,map=point=>[point[0],point[2],point[1]],points=drawing.points.map(map),width=map(drawing.widthDirection),direction=unit(sub(points[1],points[0]));
  const setting={version:originalCopperVersion,isCenter:true,startBusbarVector:cross(width,direction),widthVector:width,pointList:points,busBarWidth:drawing.widthMm,busBarThickness:drawing.thicknessMm,barBendingRadius:drawing.flatRadiusMm,verticalBendingRadius:drawing.edgeRadiusMm,bendAllowance:20,startLapWidth:0,endLapWidth:0,groupKey:'',planningSegmentIndex:null,isVerticalBend:false,isMilledBar:false};
  originalCopperPreviewDefinition(setting);return setting;
}

export function createOriginalCopperObject(setting,name='原方案铜排',phase=null){
  if(setting.isVerticalBend||setting.isMilledBar){
    originalCopperSectionFrames(setting);
    return {id:crypto.randomUUID(),kind:'original-copper',name,category:'copper',parentId:null,templatePath:'free-draw/original-busbar',configuration:'原方案绘制',sourceSha256:null,parameters:{},suppression:{},transform:[1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1],visible:true,phase,originalCopper:structuredClone(setting)};
  }
  const object=createFreeCopperObject(originalCopperPreviewDefinition(setting),name,phase);
  object.originalCopper=structuredClone(setting);return object;
}

export function validateOriginalCopperObject(object){
  if(!object.originalCopper)return;
  if(Object.keys(object.parameters??{}).length)throw new DefinitionError('原方案铜排尺寸必须通过界面铜排组修改');
  if(object.kind==='original-copper'){originalCopperSectionFrames(object.originalCopper);if(object.freeCopper)throw new DefinitionError('原混合工艺铜排不能附加另一条预览路径');return;}
  const definition=planFreeCopper(originalCopperPreviewDefinition(object.originalCopper)).data,actual=planFreeCopper(object.freeCopper).data;
  // Holes are authored on preview straight segments, and remain independent
  // of the source drawing's PointList. All other preview data is derived.
  for(const name of ['version','points','widthDirection','widthMm','thicknessMm','flatRadiusMm','edgeRadiusMm'])if(JSON.stringify(actual[name])!==JSON.stringify(definition[name]))throw new DefinitionError('原方案铜排参考线与派生预览不一致：'+name);
}

export function copperDimensionUpdate(object,patch){
  if(!object.originalCopper)return {freeCopper:planFreeCopper({...planFreeCopper(object.freeCopper,object.parameters).data,...patch}).data};
  validateOriginalCopperObject(object);
  const originalCopper=structuredClone(object.originalCopper),mapping={widthMm:'busBarWidth',thicknessMm:'busBarThickness',flatRadiusMm:'barBendingRadius',edgeRadiusMm:'verticalBendingRadius'};
  for(const [name,value]of Object.entries(patch)){if(!mapping[name])throw new DefinitionError('原方案铜排包含未知界面尺寸');originalCopper[mapping[name]]=value;}
  const freeCopper={...originalCopperPreviewDefinition(originalCopper),holes:structuredClone(object.freeCopper.holes??[])};
  planFreeCopper(freeCopper);return {originalCopper,freeCopper};
}

export function evaluateOriginalCopper(kernel,setting,settings={},holes=[]){
  const result=evaluateFreeCopper(kernel,{freeCopper:{...originalCopperPreviewDefinition(setting),holes},parameters:{}},settings);
  result.derivedGeometry={...result.derivedGeometry,kind:'original-planar-bend-copper',drawingDevelopedLengthMm:originalCopperDevelopedLength(setting),drawingReferenceConvention:setting.pathContract==='native-material'?'native-material-section-center':'original-IsCenter-minus-half-thickness-offset',groupKey:setting.groupKey,planningSegmentIndex:setting.planningSegmentIndex};return result;
}

// 对照原 VerticalBendPreviewAdapter：原路径始终按截面中心解释；其 -半厚
// 侧面偏移在 VerticalBendPreviewBody 的单侧拉伸中抵消，不能再次偏移中心线。
// GetWidthVector 使用 路径方向 × 起始排厚方向；之后沿平弯、立弯连续传递截面。
export function planOriginalVerticalCopper(setting){
  if(setting?.version!==originalCopperVersion||!setting.isVerticalBend||setting.isMilledBar||typeof setting.isCenter!=='boolean')
    throw new DefinitionError('原立弯铜排定义无效');
  if(!Array.isArray(setting.pointList)||setting.pointList.length<2)throw new DefinitionError('原立弯铜排路径至少需要两个点');
  const points=setting.pointList.map(point=>vector(point,'原立弯铜排路径点')),start=vector(setting.startBusbarVector,'原立弯起始排厚方向'),
    first=sub(points[1],points[0]),width=cross(first,start);
  if(norm(first)<=1e-8||norm(start)<=1e-8||norm(width)<=1e-8)throw new DefinitionError('原立弯起始路径和排厚方向无效');
  const map=point=>[point[0],(setting.coordinateFrame==='solidworks'?-1:1)*point[2],point[1]];
  return planFreeCopper({version:freeCopperVersion,points:points.map(map),widthDirection:map(unit(width)),
    widthMm:setting.busBarWidth,thicknessMm:setting.busBarThickness,flatRadiusMm:setting.barBendingRadius,
    edgeRadiusMm:setting.verticalBendingRadius>0?setting.verticalBendingRadius:setting.barBendingRadius,holes:[]});
}

function evaluateOriginalVerticalCopper(kernel,object,settings){
  const definition=planOriginalVerticalCopper(object.originalCopper).data,origin=definition.points[0],
    localDefinition={...definition,points:definition.points.map(point=>sub(point,origin))},
    result=evaluateFreeCopper(kernel,{freeCopper:localDefinition,parameters:{}},settings);
  let installed;
  try{
    const local=result.solids[0],components=local.decompose();
    try{if(components.length!==1)throw new DefinitionError('立弯铜排的直段与圆弧未形成连续实体');}
    finally{components.forEach(component=>component.delete());}
    const localMesh=solidToMesh(local);
    installed=local.translate(origin);
    // 切线边线与网格都在同一个局部坐标系，安装平移在显示和物理检查边界统一应用。
    return {...result,solids:[installed],parameters:[],solidSources:[{path:'original-vertical-copper-body',bodyIndex:0,
      componentChain:[],localMesh,transform:[1,0,0,0,0,1,0,0,0,0,1,0,...origin,1],
      shapeInputSignature:JSON.stringify(object.originalCopper)}],derivedGeometry:{...result.derivedGeometry,
      kind:'original-mixed-copper',source:'VerticalBendPreviewBody section placement / continuous rectangular-section sweep',
      drawingDevelopedLengthMm:originalCopperDevelopedLength(object.originalCopper),drawingReferenceConvention:'original-vertical-section-centre'}};
  }catch(error){installed?.delete();throw error;}
  finally{result.solids.forEach(solid=>solid.delete());}
}

export function evaluateCopperObject(kernel,object,settings={}){
  if(object.kind==='original-copper'&&object.originalCopper.isVerticalBend&&!object.originalCopper.isMilledBar){
    validateOriginalCopperObject(object);return evaluateOriginalVerticalCopper(kernel,object,settings);
  }
  if(object.kind==='original-copper'){
    validateOriginalCopperObject(object);const started=performance.now(),sections=[];let combined;
    try{
      // Mathematical port of original BridgeCopperClearance.CreateFixedInterfacePreviewSolids.
      // The source preview uses each path segment's projected section, including
      // mixed/vertical/milled bars. Only these live solids are generated here.
      const frames=originalCopperSectionFrames(object.originalCopper);
      for(const frame of frames){
        const cube=kernel.Manifold.cube([frame.length,object.originalCopper.busBarWidth,object.originalCopper.busBarThickness],true);
        const transform=[...frame.direction,0,...frame.width,0,...frame.thickness,0,...frame.center,1];
        try{
          sections.push(cube.transform(transform));
        }finally{cube.delete();}
      }
      // 这些截面是同一根实体铜排的构造分段，相邻段在折点处共同组成材料。
      // 非相邻段仍严格检查自身相交；合并后再让装配检查比较不同物理零件。
      for(let first=0;first<sections.length;first++)for(let second=first+2;second<sections.length;second++){
        const common=sections[first].intersect(sections[second]);
        try{
          if(common.status()!=='NoError')throw new DefinitionError('混合工艺铜排自身相交检查失败');
          if(common.isEmpty())continue;
          const bounds=common.boundingBox(),volume=common.volume();
          if(!Number.isFinite(volume))throw new DefinitionError('混合工艺铜排自身相交体积无效');
          if(bounds.min.every((value,axis)=>bounds.max[axis]-value>.001)&&Math.abs(volume)>common.surfaceArea()*common.tolerance())
            throw new DefinitionError('铜排非相邻路径段自身相交，请重新规划：'+object.name);
        }finally{common.delete();}
      }
      combined=kernel.Manifold.union(sections);
      if(combined.status()!=='NoError'||combined.isEmpty())throw new DefinitionError('混合工艺铜排分段不能形成有效实体');
      // 网格留在零件局部附近，绝对安装平移单独保留双精度，避免大坐标写入Float32损伤临界净距。
      const origin=frames[0].center,local=combined.translate(origin.map(value=>-value));let localMesh;
      try{localMesh=solidToMesh(local);}finally{local.delete();}
      const solidSources=[{path:'original-copper-body',bodyIndex:0,componentChain:[],localMesh,
        transform:[1,0,0,0,0,1,0,0,0,0,1,0,...origin,1],shapeInputSignature:JSON.stringify(object.originalCopper)}];
      const solid=combined;combined=null;
      return {solids:[solid],solidSources,parameters:[],parameterMs:0,geometryTimings:{otherFeatureMs:performance.now()-started,cutFeatureMs:0},preview:{complexFeatureCount:0,canSimplify:false,coarsened:false},derivedGeometry:{kind:'original-mixed-copper',source:'BridgeCopperClearance.CreateFixedInterfacePreviewSolids',drawingDevelopedLengthMm:originalCopperDevelopedLength(object.originalCopper),bends:object.originalCopper.pointList.slice(1,-1).map((_,index)=>({pathPointIndex:index+1,kind:object.originalCopper.previewBendPointKinds?.[index+1]??0})),holes:0}};
    }finally{combined?.delete();sections.forEach(solid=>solid.delete());}
  }
  if(!object.originalCopper)return evaluateFreeCopper(kernel,object,settings);
  validateOriginalCopperObject(object);return evaluateOriginalCopper(kernel,object.originalCopper,settings,object.freeCopper.holes??[]);
}

export function exportCopperDesign(object){
  if(object.kind==='original-copper'){
    validateOriginalCopperObject(object);
    return {objectId:object.id,name:object.name,phase:object.phase??null,groupId:object.copperGroupId??object.id,transform:structuredClone(object.transform),drawingMethod:'original-mixed-reference',drawing:structuredClone(object.originalCopper),drawingAxes:['length','height','width'],holes:{reference:'original-drawing',items:[]},originalDrawingDevelopedLengthMm:originalCopperDevelopedLength(object.originalCopper),meshExported:false};
  }
  if(object.kind!=='free-copper')throw new DefinitionError('铜排导出对象身份无效');
  validateOriginalCopperObject(object);const definition=planFreeCopper(object.freeCopper,object.parameters).data;
  return {objectId:object.id,name:object.name,phase:object.phase??null,groupId:object.copperGroupId??object.id,transform:structuredClone(object.transform),drawingMethod:object.originalCopper?'original-planar-reference':'rectangular-section-centerline',drawing:structuredClone(object.originalCopper??definition),drawingAxes:object.originalCopper?['length','height','width']:['length','width','height'],holes:{reference:'preview-centerline-straight-segment',segmentIndexBase:0,units:'mm',items:structuredClone(definition.holes)},originalDrawingDevelopedLengthMm:object.originalCopper?originalCopperDevelopedLength(object.originalCopper):null,meshExported:false};
}

export function originalCopperSectionFrames(setting){
  if(setting?.version!==originalCopperVersion||typeof setting.isCenter!=='boolean')throw new DefinitionError('原混合工艺铜排定义无效');
  if(setting.isVerticalBend&&!setting.isMilledBar)return planOriginalVerticalCopper(setting).segments.map(segment=>({
    center:scale(add(segment.start,segment.end),.5),direction:segment.direction,width:segment.width,
    thickness:segment.thickness,length:segment.lengthMm}));
  positive(setting.busBarWidth,'原铜排排宽');positive(setting.busBarThickness,'原铜排板厚');
  if(!Array.isArray(setting.pointList)||setting.pointList.length<2||setting.pointList.length>512)throw new DefinitionError('原铜排路径点无效');
  const points=setting.pointList.map(point=>vector(point,'原铜排路径点')),widthSource=vector(setting.widthVector,'原排宽方向'),start=vector(setting.startBusbarVector,'原板厚方向');
  if(norm(widthSource)<1e-8||norm(start)<1e-8)throw new DefinitionError('原铜排截面方向不能为零');
  const map=point=>[point[0],(setting.coordinateFrame==='solidworks'?-1:1)*point[2],point[1]];
  return points.slice(1).map((end,index)=>{
    const delta=sub(end,points[index]),span=norm(delta);if(span<=1e-8)throw new DefinitionError('原铜排相邻路径点重合');
    const direction=unit(delta),sourceWidth=unit(widthSource),projection=dot(sourceWidth,direction),projected=sub(sourceWidth,scale(direction,projection));
    if(norm(projected)<=1e-8)throw new DefinitionError('预览截面方向与铜排路径平行：'+setting.groupKey);
    const width=unit(projected);let thickness=unit(cross(direction,width));if(dot(thickness,start)<0)thickness=scale(thickness,-1);
    const center=add(scale(add(points[index],end),.5),scale(thickness,setting.isCenter?0:setting.busBarThickness/2));
    // A cube's orientation must be right handed. Its symmetric section is
    // identical after reversing the third axis; source offset already applied.
    const mappedDirection=map(direction),mappedWidth=map(width);
    return {center:map(center),direction:mappedDirection,width:mappedWidth,thickness:cross(mappedDirection,mappedWidth),length:span};
  });
}
