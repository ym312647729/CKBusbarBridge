import {DefinitionError} from './expressions.mjs';
import {inverseRigidMatrix,swMatrix,transformPoint} from './geometry.mjs';

const dot=(first,second)=>first.reduce((sum,value,index)=>sum+value*second[index],0);
const subtract=(first,second)=>first.map((value,index)=>value-second[index]);
const cross=(first,second)=>[first[1]*second[2]-first[2]*second[1],first[2]*second[0]-first[0]*second[2],first[0]*second[1]-first[1]*second[0]];
const normalized=vector=>{const length=Math.hypot(...vector);if(length<1e-10)throw new DefinitionError('拉伸解析边界退化');return vector.map(value=>value/length);};
export const samePoint=(first,second)=>first.length===second.length&&Math.hypot(...subtract(first,second))<1e-8;

// Analytic incidences of an actual straight-sided extrusion. These are derived
// from original profile identities and end conditions, not from a bounding box.
export function extrudedTopology(configuration,feature){
  const properties=feature.definition?.properties,profiles=configuration.features.filter(candidate=>candidate.sketch&&feature.parents.includes(candidate.name));
  if(!['Extrusion','Boss'].includes(feature.type)||profiles.length!==1||properties?.FromType!==0||properties.isThin||properties.FlipSideToCut||properties.Contours?.length||properties.forwardDraftEnabled||properties.reverseDraftEnabled||properties.directionReferences?.some(row=>row.reference))throw new DefinitionError('依赖边界不是可证明的原直边拉伸：'+feature.name);
  const sketch=profiles[0].sketch,matrix=inverseRigidMatrix(swMatrix(sketch.modelToSketch,1)),normal=normalized(matrix.slice(8,11)),segments=sketch.segments.filter(segment=>!segment.construction);
  if(segments.some(segment=>segment.type!==0))throw new DefinitionError('拉伸依赖边界含未解析曲线：'+feature.name);
  let lower=0,upper=properties.forwardDepth;
  if(properties.forwardEndCondition===6){lower=-upper/2;upper/=2;}
  else if(properties.forwardEndCondition===0){if(properties.ReverseDirection){lower=-upper;upper=0;}}
  else if(properties.forwardEndCondition===4){const plane=properties.forwardEndReference?.planeParameters;if(!plane||Math.abs(Math.abs(dot(normal,plane.slice(0,3)))-1)>1e-7)throw new DefinitionError('拉伸依赖终面未与草图平行');const origin=transformPoint([0,0,0],matrix);upper=dot(subtract(plane.slice(3,6),origin),plane.slice(0,3))/dot(normal,plane.slice(0,3));if(properties.ReverseDirection||upper<=0)throw new DefinitionError('拉伸依赖终面方向无效');}
  else throw new DefinitionError('拉伸依赖终止条件尚未支持');
  if(!(upper>lower))throw new DefinitionError('拉伸依赖厚度无效');
  const edges=[],vertices=[],faces=[],at=(point,level)=>transformPoint([point[0],point[1],level],matrix),addVertex=(key,point)=>vertices.push({key,point});
  for(const segment of segments){const {start,end}=segment.geometry,points=[at(start,lower),at(end,lower),at(start,upper),at(end,upper)];
    for(const [index,point]of points.entries())addVertex(segment.id+':'+index,point);
    edges.push({key:segment.id+':lower',start:points[0],end:points[1]},{key:segment.id+':upper',start:points[2],end:points[3]},{key:segment.id+':start',start:points[0],end:points[2]},{key:segment.id+':end',start:points[1],end:points[3]});
    faces.push({key:segment.id+':side',origin:points[0],normal:normalized(cross(subtract(points[1],points[0]),normal))});
  }
  if(!vertices.length)throw new DefinitionError('拉伸依赖缺少原轮廓');
  faces.push({key:'lower',origin:at(segments[0].geometry.start,lower),normal},{key:'upper',origin:at(segments[0].geometry.start,upper),normal});
  return {feature:feature.name,featureId:feature.id,profile:profiles[0].name,edges,vertices,faces};
}

export function pairedExtrudedTopology(original,updated,featureName){
  const first=original.features.find(feature=>feature.name===featureName),second=updated.features.find(feature=>feature.id===first?.id&&feature.name===featureName);
  if(!first||!second)throw new DefinitionError('受驱依赖没有同一原拉伸特征');
  const before=extrudedTopology(original,first),after=extrudedTopology(updated,second);
  const pair=(kind)=>before[kind].map(source=>{const target=after[kind].find(candidate=>candidate.key===source.key);if(!target)throw new DefinitionError('受驱依赖原拓扑身份已变化');return {source,target,feature:featureName,featureId:first.id};});
  return {feature:featureName,edges:pair('edges'),vertices:pair('vertices'),faces:pair('faces')};
}

// Through-all cuts have no finite captured depth. Their straight profile sides
// still define exact supporting planes; no end faces or vertices are invented.
export function pairedThroughCutPlanes(original,updated,featureName){
  const source=original.features.find(feature=>feature.name===featureName),target=updated.features.find(feature=>feature.id===source?.id),properties=source?.definition?.properties;
  if(source?.type!=='Cut'||target?.type!=='Cut'||properties?.forwardEndCondition!==1||properties.isThin||properties.Contours?.length||properties.FromType!==0)throw new DefinitionError('墙洞依赖不是完整原轮廓的贯穿切除');
  const faces=configuration=>{const profiles=configuration.features.filter(feature=>feature.sketch&&source.parents.includes(feature.name));if(profiles.length!==1)throw new DefinitionError('贯穿切除没有唯一原草图');const sketch=profiles[0].sketch,matrix=inverseRigidMatrix(swMatrix(sketch.modelToSketch,1)),normal=normalized(matrix.slice(8,11));return sketch.segments.filter(segment=>!segment.construction).map(segment=>{if(segment.type!==0)throw new DefinitionError('贯穿切除侧面含未解析曲线');const start=transformPoint(segment.geometry.start,matrix),end=transformPoint(segment.geometry.end,matrix);return {key:segment.id+':side',origin:start,normal:normalized(cross(subtract(end,start),normal))};});};
  const before=faces(original),after=faces(updated);
  return {feature:featureName,edges:[],vertices:[],faces:before.map(sourceFace=>{const targetFace=after.find(face=>face.key===sourceFace.key);if(!targetFace)throw new DefinitionError('贯穿切除轮廓身份发生变化');return {source:sourceFace,target:targetFace,feature:featureName,featureId:source.id};})};
}

export function mapExtrudedPlane(reference,topologies){
  const source=reference.planeParameters??(reference.referencePlaneTransform?[...reference.referencePlaneTransform.slice(6,9),...reference.referencePlaneTransform.slice(9,12)]:null);
  if(!source)throw new DefinitionError('受驱平面没有原解析定义');
  const normal=normalized(source.slice(0,3)),origin=source.slice(3,6),candidates=topologies.flatMap(topology=>topology.faces).filter(pair=>Math.abs(Math.abs(dot(normal,pair.source.normal))-1)<1e-7&&Math.abs(dot(normal,subtract(origin,pair.source.origin)))<1e-8);
  if(!candidates.length)throw new DefinitionError('受驱平面未对应原拉伸轮廓面');
  const distances=candidates.map(pair=>{if(Math.abs(Math.abs(dot(normal,pair.target.normal))-1)>1e-7)throw new DefinitionError('受驱引用面的旋转已变化');return dot(normal,subtract(pair.target.origin,pair.source.origin));});
  if(distances.some(distance=>Math.abs(distance-distances[0])>1e-8))throw new DefinitionError('同平面的原拉伸面在驱动后不能证明等价');
  const updated=structuredClone(reference),delta=normal.map(value=>value*distances[0]);
  if(updated.planeParameters)updated.planeParameters=[...source.slice(0,3),...origin.map((value,index)=>value+delta[index])];
  if(updated.referencePlaneTransform)for(let axis=0;axis<3;axis++)updated.referencePlaneTransform[9+axis]+=delta[axis];
  return {reference:updated,delta,proof:candidates.map(pair=>({feature:pair.feature,key:pair.source.key}))};
}

export function mapDrivenReferencePlane(reference,configuration,topologies){
  if(reference?.planeParameters)return mapExtrudedPlane(reference,topologies);
  const matrix=reference?.referencePlaneTransform,feature=configuration.features.find(feature=>feature.name===reference?.feature&&feature.type==='RefPlane'),properties=feature?.definition?.properties;
  if(!matrix||!properties||feature.dimensions?.length)throw new DefinitionError('受驱基准面没有完整原约束定义');
  if(properties.Type===10)return {reference:structuredClone(reference),delta:[0,0,0],proof:[{feature:feature.name,kind:'fixed-native-datum'}]};
  const constraints=(properties.constraintReferences??[]).filter(constraint=>constraint.constraint!==0),normal=normalized(matrix.slice(6,9));
  if(!(constraints.length===1&&constraints[0].constraint===4||constraints.length===2&&constraints.every(constraint=>constraint.constraint===128)))throw new DefinitionError('受驱基准面不是原重合或中间平面约束');
  const mapped=constraints.map(constraint=>{if(!constraint.reference?.planeParameters||constraint.angleOrDistance!==0)throw new DefinitionError('受驱基准面缺少零偏置的原实体面');const plane=constraint.reference.planeParameters;if(Math.abs(Math.abs(dot(normal,normalized(plane.slice(0,3))))-1)>1e-7)throw new DefinitionError('受驱基准面原约束法向不平行');return mapExtrudedPlane(constraint.reference,topologies);});
  const expected=constraints.reduce((sum,constraint)=>sum+dot(normal,constraint.reference.planeParameters.slice(3,6))/constraints.length,0);
  if(Math.abs(dot(normal,matrix.slice(9,12))-expected)>1e-8)throw new DefinitionError('原基准面位置不满足已捕获的约束');
  const distance=mapped.reduce((sum,item)=>sum+dot(normal,item.delta)/mapped.length,0),delta=normal.map(value=>value*distance),updated=structuredClone(reference);for(let axis=0;axis<3;axis++)updated.referencePlaneTransform[9+axis]+=delta[axis];
  return {reference:updated,delta,proof:mapped.flatMap(item=>item.proof)};
}
