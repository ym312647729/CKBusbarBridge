import {DefinitionError} from './expressions.mjs';
import {swMatrix,inverseRigidMatrix,transformPoint} from './geometry.mjs';
import {transformDirection} from './template-planes.mjs';

const canonical=name=>name.split('@').slice(0,-1).join('@');
const subtract=(first,second)=>first.map((value,axis)=>value-second[axis]);
const dot=(first,second)=>first.reduce((sum,value,axis)=>sum+value*second[axis],0);
const mm=point=>point.map(value=>value*1000),metres=point=>point.map(value=>Math.fround(value/1000));
const same=(first,second)=>Math.hypot(...subtract(mm(first),mm(second)))<=1;
const one=(rows,label)=>{if(rows.length!==1)throw new DefinitionError(label+'未唯一对应');return rows[0];};

// Original 001 changes the horizontal leg (b), 003 the vertical leg (D2).
// Translate the native tip and its fillet, retaining thickness, root fillet,
// tangent endpoints and all original segment identities. No scaled stock mesh.
export function driveSmallOffsetAngleProfile(template,configuration,model,updated){
  const nativePath=template.source.nativeTemplatePath??template.source.path;
  const variant=nativePath==='模板/基架模板/小偏移基架/8GX.160.001.SLDPRT'?{name:'b@Sketch1',axis:0,sign:1}:nativePath==='模板/基架模板/小偏移基架/8GX.160.003.SLDPRT'?{name:'D2@Sketch1',axis:1,sign:-1}:null;
  if(!variant)return null;
  const driven=model.dimensions.find(row=>row.dimension===variant.name);
  if(!driven||driven.valueMm===driven.sourceValueMm)return null;
  const profile=one(configuration.features.filter(feature=>feature.name==='Sketch1'&&feature.sketch),'032原角钢截面'),sketch=profile.sketch,sourceWidth=Math.fround(driven.sourceValueMm),width=Math.fround(driven.valueMm),delta=Math.fround(width-sourceWidth),station=point=>point[variant.axis]*1000*variant.sign;
  const dimension=one(profile.dimensions.filter(row=>canonical(row.name)===variant.name),'032原角钢宽度');
  if(Math.abs(dimension.value*1000-sourceWidth)>1||width<40||width>70||sketch.segments.filter(row=>row.type===1).length!==3)throw new DefinitionError('032角钢截面不符合原40至70 mm加宽规则');
  const tip=one(sketch.segments.filter(segment=>segment.type===0&&!segment.construction&&[segment.geometry.start,segment.geometry.end].every(point=>Math.abs(station(point)-sourceWidth)<=1)),'032原角钢外端边');
  const fillet=one(sketch.segments.filter(segment=>segment.type===1&&[segment.geometry.start,segment.geometry.end].some(point=>[tip.geometry.start,tip.geometry.end].some(end=>same(point,end)))),'032原角钢端部圆角');
  const translated=[tip.geometry.start,tip.geometry.end,fillet.geometry.start,fillet.geometry.end,fillet.geometry.center],move=point=>{const value=[...point];value[variant.axis]=Math.fround(value[variant.axis]+variant.sign*delta/1000);return value;};
  const vertex=point=>translated.some(candidate=>same(candidate,point))?move(point):[...point];
  const changed=structuredClone(sketch);
  for(const segment of changed.segments){
    const original=one(sketch.segments.filter(row=>row.id===segment.id),'032原截面线');
    for(const key of ['start','end'])segment.geometry[key]=vertex(original.geometry[key]);
    if(segment.type===1)segment.geometry.center=original.id===fillet.id?move(original.geometry.center):[...original.geometry.center];
  }
  function pointPosition(point){
    if(translated.some(candidate=>same(candidate,point)))return move(point);
    for(const segment of sketch.segments.filter(row=>row.type===0)){
      const start=mm(segment.geometry.start),direction=subtract(mm(segment.geometry.end),start),lengthSquared=dot(direction,direction),parameter=dot(subtract(mm(point),start),direction)/lengthSquared;
      if(parameter<0||parameter>1||Math.hypot(...subtract(mm(point),start).map((value,axis)=>value-parameter*direction[axis]))>1)continue;
      const target=changed.segments.find(row=>row.id===segment.id);
      return target.geometry.start.map((value,axis)=>Math.fround(value+parameter*(target.geometry.end[axis]-value)));
    }
    return [...point];
  }
  for(const point of changed.points)point.coordinates=pointPosition(point.coordinates);
  for(const relation of changed.relations){
    if(relation.dimension&&canonical(relation.dimension.name)===variant.name)relation.dimension.value=width/1000;
    for(const entity of relation.entities)if(entity.point&&changed.points.some(point=>point.id===entity.id))entity.point=[...changed.points.find(point=>point.id===entity.id).coordinates];
  }
  updated.features.find(feature=>feature.id===profile.id).sketch=changed;
  const toModel=inverseRigidMatrix(swMatrix(sketch.modelToSketch)),toSketch=swMatrix(sketch.modelToSketch),profilePlanes=[];
  for(const segment of sketch.segments.filter(row=>row.type===0&&!row.construction)){
    const other=changed.segments.find(row=>row.id===segment.id),direction=subtract(segment.geometry.end,segment.geometry.start),normal=transformDirection([-direction[1],direction[0],0],toModel),magnitude=Math.hypot(...normal);
    profilePlanes.push({point:transformPoint(mm(segment.geometry.start),toModel),newPoint:transformPoint(mm(other.geometry.start),toModel),normal:normal.map(value=>value/magnitude)});
  }
  const widthDirection=transformDirection([0,1,2].map(axis=>axis===variant.axis?variant.sign:0),toModel);
  const pointDisplacement=point=>station(metres(transformPoint(point,toSketch)))>=sourceWidth-fillet.geometry.radius*1000?widthDirection.map(value=>Math.fround(value*delta)):[0,0,0];
  return {dimension:variant.name,profilePlanes,pointDisplacement,evidence:{source:'native-angle-leg-dimension-and-tangent-tip-fillet',dimension:variant.name,sourceWidth,width,fillet:fillet.id,thicknessAndRootFilletPreserved:true}};
}

// Suppressed sketches can contain stale proxy coordinates. Bind the actual
// native parent feature and sketch-point identities captured with rollback,
// then read absolute endpoints from that already driven parent.
export function resolveSmallOffsetAngleDependency(configuration,updated,profile,reference){
  const rows=(configuration.angleDependencyReferences??[]).filter(row=>row.profileId===profile.id&&row.referenceId===reference.id);
  if(!rows.length)return null;
  const binding=one(rows,'032原父特征引用'),owner=one(updated.features.filter(row=>row.id===binding.ownerFeatureId),'032原父特征'),parent=one(updated.features.filter(row=>row.id===binding.parentProfileId&&row.sketch),'032原父草图');
  if(owner.suppressed||!owner.parents.includes(parent.name)||!binding.nativeEntityId||!['vertex','edge'].includes(binding.kind))throw new DefinitionError('032切除引用未对应启用的原父特征');
  const toModel=inverseRigidMatrix(swMatrix(parent.sketch.modelToSketch)),points=binding.parentPointIds.map(id=>transformPoint(mm(one(parent.sketch.points.filter(point=>point.id===id),'032原父草图点').coordinates),toModel));
  if(binding.kind==='vertex'){
    if(points.length!==1)throw new DefinitionError('032原顶点引用不唯一');
    return {...reference,point:metres(transformPoint(points[0],swMatrix(profile.sketch.modelToSketch)))};
  }
  if(points.length!==2)throw new DefinitionError('032原边引用端点不完整');
  const direction=subtract(points[1],points[0]),length=Math.hypot(...direction);
  if(!(length>0))throw new DefinitionError('032原父特征边退化');
  return {...reference,start:metres(points[0]),end:metres(points[1]),lineParameters:[...metres(points[0]),...direction.map(value=>Math.fround(value/length))]};
}
