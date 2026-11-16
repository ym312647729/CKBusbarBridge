import {DefinitionError} from './expressions.mjs';
import {resolveRouteEndpoints} from './template-interfaces.mjs';

const tolerance=.001;
export const pathRoles={ordinary:0,phaseChange:1,offset:2};
export const planningRules={version:'dq-offline-path/1',preferredBreakpointSpacing:2000,maximumDevelopedLength:3000,minimumFrameLength:200,maximumFrameLength:1400,standardFrameLength:1000};
const finite=(value,label)=>{if(typeof value!=='number'||!Number.isFinite(value))throw new DefinitionError(label+'必须是有限数值');return value;};
const positive=(value,label)=>{finite(value,label);if(value<=0)throw new DefinitionError(label+'必须大于零');return value;};
const nonnegative=(value,label)=>{finite(value,label);if(value<0)throw new DefinitionError(label+'不能小于零');return value;};
function offsetLength(dx,dy,orthogonal){return orthogonal?Math.abs(dx)+Math.abs(dy):Math.abs(dx)/2+Math.hypot(dx/2,dy);}
function segment(index,start,end,role,overlapLength){return {index,start,end,role,centerlineLength:end.pathDistance-start.pathDistance,developedLength:end.pathDistance-start.pathDistance+overlapLength};}

// Ported from the project's migrated MiddleBusbarPathPlanner; no existing frame coordinates are inputs.
export function planMiddlePath(input){
  const settings={preferredBreakpointSpacing:2000,maximumDevelopedLength:3000,offsetProtectionLength:0,requiresPhaseChange:false,useOrthogonalOffsetTransition:false,deferOffsetLengthLimitToPhysicalGeometry:false,...input};
  for(const key of ['startX','startY','endX','endY'])finite(settings[key],key);
  positive(settings.preferredBreakpointSpacing,'优选断点间距');positive(settings.maximumDevelopedLength,'最大展开长度');
  if(settings.maximumDevelopedLength>3000+tolerance)throw new DefinitionError('最大展开长度不能超过3000');
  nonnegative(settings.overlapLength,'实际搭接长度');nonnegative(settings.offsetProtectionLength,'偏移保护长度');
  const span=settings.endX-settings.startX,offset=settings.endY-settings.startY,maximumCenterline=settings.maximumDevelopedLength-settings.overlapLength;
  if(span<=tolerance)throw new DefinitionError('A端接口必须位于B端接口的路径前方');
  if(maximumCenterline<=tolerance)throw new DefinitionError('最大展开长度必须大于实际搭接长度');
  if(settings.requiresPhaseChange)nonnegative(settings.phaseChangeInsulationClearance,'换相绝缘距离');
  const vertices=[{x:settings.startX,y:settings.startY,endsOffset:false}];let offsetFrameStartX=null,offsetFrameEndX=null;
  if(Math.abs(offset)>tolerance){
    positive(settings.minimumOffsetTransitionSpan,'偏移基架纵向长度');
    if(!settings.deferOffsetLengthLimitToPhysicalGeometry&&Math.abs(offset)+settings.overlapLength>settings.maximumDevelopedLength+tolerance)throw new DefinitionError('横向偏移超过单根铜排最大展开长度');
    const frameSpan=settings.minimumOffsetTransitionSpan,transitionSpan=frameSpan+2*settings.offsetProtectionLength;
    if(transitionSpan>span+tolerance)throw new DefinitionError('偏移过渡区最小长度超过两端接口可用跨度');
    if(!settings.deferOffsetLengthLimitToPhysicalGeometry&&offsetLength(transitionSpan,offset,settings.useOrthogonalOffsetTransition)>maximumCenterline+tolerance)throw new DefinitionError('偏移过渡段超过3000最大展开长度，不能在工艺区内断开');
    const center=(settings.startX+settings.endX)/2,left=Math.max(settings.startX,center-transitionSpan/2),right=Math.min(settings.endX,center+transitionSpan/2);
    offsetFrameStartX=center-frameSpan/2;offsetFrameEndX=center+frameSpan/2;
    if(left>settings.startX+tolerance)vertices.push({x:left,y:settings.startY,endsOffset:false});
    vertices.push({x:right,y:settings.endY,endsOffset:true});
  }
  vertices.push({x:settings.endX,y:settings.endY,endsOffset:false});
  const points=[],offsetIndices=new Set();
  for(let vertexIndex=0;vertexIndex<vertices.length-1;vertexIndex++){
    const start=vertices[vertexIndex],end=vertices[vertexIndex+1],length=Math.hypot(end.x-start.x,end.y-start.y);
    if(length<=tolerance)continue;
    let partCount=1;
    if(!end.endsOffset){
      const minimumCount=Math.max(1,Math.ceil((length-tolerance)/maximumCenterline)),idealCount=Math.max(1,Math.round(length/settings.preferredBreakpointSpacing));
      partCount=minimumCount;let deviation=Math.abs(length/partCount-settings.preferredBreakpointSpacing);
      for(let count=minimumCount+1;count<=Math.max(minimumCount,idealCount+1);count++){
        if(length/count>maximumCenterline+tolerance)continue;
        const candidateDeviation=Math.abs(length/count-settings.preferredBreakpointSpacing);
        if(candidateDeviation<deviation-tolerance){partCount=count;deviation=candidateDeviation;}
      }
    }
    if(length/partCount>maximumCenterline+tolerance&&!(end.endsOffset&&settings.deferOffsetLengthLimitToPhysicalGeometry))throw new DefinitionError('单段无法满足3000最大展开长度');
    for(let partIndex=0;partIndex<=partCount;partIndex++){
      if(vertexIndex>0&&partIndex===0)continue;
      const fraction=partIndex/partCount,x=start.x+(end.x-start.x)*fraction,y=start.y+(end.y-start.y)*fraction,previous=points.at(-1);
      const pathDistance=previous?previous.pathDistance+(end.endsOffset?offsetLength(x-previous.x,y-previous.y,settings.useOrthogonalOffsetTransition):Math.hypot(x-previous.x,y-previous.y)):0;
      points.push({x,y,pathDistance});if(end.endsOffset&&points.length>=2)offsetIndices.add(points.length-2);
    }
  }
  if(points.length<2)throw new DefinitionError('两端接口之间没有可规划的铜排路径');
  const segments=points.slice(1).map((end,index)=>segment(index,points[index],end,(settings.requiresPhaseChange&&index===0?1:0)|(offsetIndices.has(index)?2:0),settings.overlapLength));
  for(const item of segments)if(item.developedLength>settings.maximumDevelopedLength+tolerance&&!(settings.deferOffsetLengthLimitToPhysicalGeometry&&(item.role&2)))throw new DefinitionError('规划存在超过3000最大展开长度的铜排段');
  if(settings.requiresPhaseChange&&segments[0].centerlineLength+tolerance<2*settings.phaseChangeInsulationClearance)throw new DefinitionError('第一段没有足够空间完成换相绝缘距离约束');
  return {points,segments,offsetFrameStartX,offsetFrameEndX,isLargeOffset:settings.useOrthogonalOffsetTransition,physicalSubdivisionRequired:!!settings.deferOffsetLengthLimitToPhysicalGeometry&&segments.some(item=>item.developedLength>3000+tolerance),input:structuredClone(settings),ruleVersion:planningRules.version};
}

export function movePathBreakpoints(source,coordinates){
  if(!Array.isArray(coordinates)||coordinates.length!==source.points.length||!coordinates.every(Number.isFinite))throw new DefinitionError('断点坐标必须完整且有限');
  if(Math.abs(coordinates[0]-source.points[0].x)>tolerance||Math.abs(coordinates.at(-1)-source.points.at(-1).x)>tolerance)throw new DefinitionError('两端固定接口不允许移动');
  const points=[],segments=[];
  for(let index=0;index<coordinates.length;index++){
    const x=coordinates[index],y=source.points[index].y,previous=points.at(-1);
    if(previous&&x-previous.x<=tolerance)throw new DefinitionError('断点不能越过相邻断点');
    const previousRole=source.segments[index-1]?.role??0;
    const pathDistance=previous?previous.pathDistance+((previousRole&2)?offsetLength(x-previous.x,y-previous.y,source.isLargeOffset):Math.hypot(x-previous.x,y-previous.y)):0;
    points.push({x,y,pathDistance});
    if(previous){const item=segment(index-1,previous,points[index],previousRole,source.input.overlapLength);if(item.developedLength>source.input.maximumDevelopedLength+tolerance)throw new DefinitionError('移动后单段展开长度超过3000');segments.push(item);}
  }
  if(source.input.requiresPhaseChange&&segments[0].centerlineLength+tolerance<2*source.input.phaseChangeInsulationClearance)throw new DefinitionError('移动后第一段不满足换相绝缘距离');
  const offset=segments.find(item=>item.role&2),frameLength=offset?source.offsetFrameEndX-source.offsetFrameStartX:0,center=offset?(offset.start.x+offset.end.x)/2:0;
  if(offset&&offset.end.x-offset.start.x+tolerance<frameLength+2*source.input.offsetProtectionLength)throw new DefinitionError('移动后偏移过渡段不足以容纳原基架及保护区');
  return {...structuredClone(source),points,segments,offsetFrameStartX:offset?center-frameLength/2:null,offsetFrameEndX:offset?center+frameLength/2:null};
}

export function regularFrameLengths(span){
  positive(span,'基架跨度');const units=Math.round(span*10);
  if(!Number.isSafeInteger(units))throw new DefinitionError('基架跨度超出可计算精度');
  if(units<2000)throw new DefinitionError('普通基架区间不足200');
  if(units<=13000)return [units/10];
  const standardCount=Math.floor(units/10000),remainder=units%10000,leftCount=Math.floor(standardCount/2),lengths=[...Array(leftCount).fill(10000),...(remainder?[remainder]:[]),...Array(standardCount-leftCount).fill(10000)];
  while(true){
    const shortIndex=lengths.findIndex(length=>length>0&&length<=3000);if(shortIndex<0)break;
    if(shortIndex+1<lengths.length&&lengths[shortIndex]+lengths[shortIndex+1]<=14000){lengths[shortIndex+1]+=lengths[shortIndex];lengths.splice(shortIndex,1);}
    else if(shortIndex>0&&lengths[shortIndex]+lengths[shortIndex-1]<=14000){lengths[shortIndex-1]+=lengths[shortIndex];lengths.splice(shortIndex,1);}
    else throw new DefinitionError('普通基架无法在200至1400范围内铺排');
  }
  return lengths.map(length=>length/10);
}

export function classifyOffset(signedOffset,aBaseWidth,bBaseWidth){
  finite(signedOffset,'偏移');positive(aBaseWidth,'A侧连接基座宽');positive(bBaseWidth,'B侧连接基座宽');
  const absoluteOffset=Math.abs(signedOffset),averageWidth=aBaseWidth/2+bBaseWidth/2;
  return absoluteOffset<=.001?'none':absoluteOffset<=200.1?'widened':absoluteOffset>averageWidth+200?'large':'small';
}

export function sizeOffsetFrame({signedOffset,phaseSpacing,busbarWidth,busbarThickness,insulationClearance=125,aBaseWidth,bBaseWidth}){
  positive(phaseSpacing,'相间中心距');positive(busbarWidth,'排宽');positive(busbarThickness,'排厚');positive(insulationClearance,'绝缘距离');
  const classification=classifyOffset(signedOffset,aBaseWidth,bBaseWidth),absoluteOffset=Math.abs(signedOffset);
  if(classification==='none')return {classification,transitionKind:null,rawMinimum:null,initialLength:null};
  let transitionKind,rawMinimum;
  if(classification==='large'){transitionKind='orthogonal';rawMinimum=2*phaseSpacing+busbarWidth+2*(30+insulationClearance+5);}
  else{
    transitionKind=absoluteOffset>busbarWidth+tolerance?'middle-parallel':busbarWidth>80?'milled':'vertical-bend';
    let transitionLength;
    if(transitionKind==='middle-parallel')transitionLength=2*phaseSpacing+busbarWidth;
    else if(transitionKind==='milled')transitionLength=3*(busbarWidth+20)+2*insulationClearance;
    else{
      const bendSpan=2*busbarWidth;
      if(absoluteOffset>=bendSpan)throw new DefinitionError('小偏移立弯偏移必须小于两个排宽');
      const nominalProjection=Math.sqrt(bendSpan*bendSpan-absoluteOffset*absoluteOffset),normalSpacing=busbarWidth+insulationClearance,difference=phaseSpacing*phaseSpacing-normalSpacing*normalSpacing;
      if(difference<=.001)throw new DefinitionError('立弯相间中心距必须大于排宽与绝缘距离之和，延长过渡段仍不能满足净距');
      const stagger=busbarThickness*absoluteOffset,clearanceProjection=(phaseSpacing*stagger+normalSpacing*Math.sqrt(stagger*stagger+difference*absoluteOffset*absoluteOffset))/difference;
      transitionLength=Math.max(nominalProjection,clearanceProjection)+2*busbarThickness;
    }
    rawMinimum=transitionLength+2*(40+insulationClearance+5);
  }
  const initialLength=Math.ceil(rawMinimum/10)*10;
  if(initialLength>1400+.001)throw new DefinitionError('特殊基架工艺长度向上取整后超过1400');
  return {classification,transitionKind,rawMinimum,initialLength};
}

export function createRouteDraft(input){
  if(!Array.isArray(input.start)||!Array.isArray(input.end)||input.start.length!==3||input.end.length!==3||![...input.start,...input.end].every(Number.isFinite))throw new DefinitionError('两端接口必须提供完整XYZ坐标');
  if(Math.abs(input.start[2]-input.end[2])>.1)throw new DefinitionError('两端接口高度不同，尚需竖向过渡规划');
  const sizing=sizeOffsetFrame({...input,signedOffset:input.end[1]-input.start[1]});
  let path=planMiddlePath({startX:input.start[0],startY:input.start[1],endX:input.end[0],endY:input.end[1],overlapLength:input.busbarWidth,minimumOffsetTransitionSpan:sizing.initialLength??0,offsetProtectionLength:sizing.initialLength?input.busbarWidth:0,useOrthogonalOffsetTransition:sizing.classification==='large',requiresPhaseChange:input.requiresPhaseChange,phaseChangeInsulationClearance:input.insulationClearance??125});
  if(input.breakpointCoordinates)path=movePathBreakpoints(path,input.breakpointCoordinates);
  return {id:crypto.randomUUID(),name:input.name||'中间铜排路径',kind:'middle-path-draft',status:'planning',input:structuredClone(input),sizing,path,elevation:input.start[2],source:'迁移原项目路径/工艺计算规则',pending:['三相物理铜排、搭接与开孔','实际基架模板驱动及安装','绝缘子支撑和净距校验']};
}

export function validateRouteDraft(route,objects){
  if(route?.kind!=='middle-path-draft'||!route.id)throw new DefinitionError('铜排路径类型或标识无效');
  const calculated=createRouteDraft(objects?resolveRouteEndpoints(route.input,objects):route.input);
  return {...calculated,id:route.id,name:route.name||calculated.name};
}

export function routePolyline(route){
  const vertices=[];
  for(const item of route.path.segments){
    if(!vertices.length)vertices.push([item.start.x,item.start.y,route.elevation]);
    if(item.role&2){
      const dx=item.end.x-item.start.x,dy=item.end.y-item.start.y;
      if(route.path.isLargeOffset){const center=(item.start.x+item.end.x)/2;vertices.push([center,item.start.y,route.elevation],[center,item.end.y,route.elevation]);}
      else vertices.push([item.start.x+dx/4,item.start.y,route.elevation],[item.start.x+dx*3/4,item.start.y+dy,route.elevation]);
    }
    vertices.push([item.end.x,item.end.y,route.elevation]);
  }
  return vertices;
}
