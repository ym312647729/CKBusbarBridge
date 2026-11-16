import {DefinitionError} from './expressions.mjs';
import {driveStraightWeldDefinition,weldReferenceDisplacement} from './driven-weld.mjs';
import {solveAssemblyTranslations} from './assembly-translations.mjs';
import {swMatrix,inverseRigidMatrix,transformPoint} from './geometry.mjs';

const near=(a,b,tolerance=1e-7)=>Math.hypot(...a.map((value,index)=>value-b[index]))<tolerance;
const canonical=name=>name.split('@').slice(0,-1).join('@');
const vector=(direction,matrix)=>[0,1,2].map(axis=>matrix[axis]*direction[0]+matrix[4+axis]*direction[1]+matrix[8+axis]*direction[2]);
const one=(rows,label)=>{if(rows.length!==1)throw new DefinitionError(label+'缺失或不唯一');return rows[0];};
const shifted=(transform,delta)=>{const updated=[...transform];for(let axis=0;axis<3;axis++)updated[9+axis]+=delta[axis]/1000;return updated;};

export function deriveWallBracketRail(source,model){
  if(source.source.path!==model.templatePath||source.source.sha256!==model.sourceSha256||!/^8GX\.160\.02[45]$/.test(model.modelId))throw new DefinitionError('穿墙边框不是原业务定义');
  const original=one(source.configurations.filter(row=>row.name===model.configuration),'原边框配置'),template=structuredClone(source),configuration=template.configurations.find(row=>row.name===model.configuration),base=one(original.features.filter(feature=>feature.type==='SMBaseFlange'),'原边框基体'),length=model.externalDimensions[0].valueMm/1000,oldLength=base.definition.properties.D1EndConditionDistance,slot=one(original.features.filter(feature=>feature.name==='草图6'&&feature.sketch),'原边框腰孔');
  if(base.definition.properties.D1EndConditionType!==1||!near(inverseRigidMatrix(swMatrix(original.features.find(feature=>feature.name==='草图1').sketch.modelToSketch,1)).slice(8,11),[0,-1,0])||!(oldLength>0&&length>0))throw new DefinitionError('穿墙边框不是原单向轴向拉伸');
  const midpoint=one(slot.sketch.relations.filter(relation=>relation.type===12&&relation.entities.some(entity=>entity.lineParameters&&entity.start&&entity.end)),'原腰孔中点关系'),reference=one(midpoint.entities.filter(entity=>entity.lineParameters),'原腰孔中点边'),point=one(midpoint.entities.filter(entity=>entity.point),'原腰孔中点点');
  if(!near(reference.start.map((value,index)=>(value+reference.end[index])/2),transformPoint(point.point,inverseRigidMatrix(swMatrix(slot.sketch.modelToSketch,1))))||Math.abs(reference.start[1]+oldLength)>1e-7||Math.abs(reference.end[1])>1e-7)throw new DefinitionError('腰孔中点没有对应原基体两端');
  delete configuration.validation;const targetBase=configuration.features.find(feature=>feature.id===base.id);for(const key of ['D1OffsetDistance','D1EndConditionDistance'])targetBase.definition.properties[key]=length;
  const targetSlot=configuration.features.find(feature=>feature.id===slot.id),movement=vector([0,-(length-oldLength)/2,0],swMatrix(slot.sketch.modelToSketch,1));
  for(const segment of targetSlot.sketch.segments)for(const key of ['start','end','center'])if(segment.geometry[key])segment.geometry[key]=segment.geometry[key].map((value,axis)=>value+movement[axis]);
  for(const point of targetSlot.sketch.points)point.coordinates=point.coordinates.map((value,axis)=>value+movement[axis]);
  const pattern=one(original.features.filter(feature=>feature.type==='LPattern'),'原边框孔阵列'),properties=pattern.definition.properties,updated=configuration.features.find(feature=>feature.id===pattern.id).definition.properties,values=Object.fromEntries(model.dimensions.map(dimension=>[dimension.dimension,dimension.value??dimension.valueMm]));
  const directions=[1,2].map(index=>{const direction=properties['D'+index+'Axis']?.lineParameters?.slice(3,6);if(!direction||Math.abs(Math.hypot(...direction)-1)>1e-7)throw new DefinitionError('孔阵列缺少原直边方向');return direction.map(value=>value*(properties['D'+index+'ReverseDirection']?1:-1));});
  const matrices=(count1,count2,spacing1,spacing2)=>Array.from({length:count1*count2},(_,index)=>{const first=index%count1,second=Math.floor(index/count1);return [1,0,0,0,1,0,0,0,1,...[0,1,2].map(axis=>directions[0][axis]*first*spacing1+directions[1][axis]*second*spacing2),1,0,0,0];});
  const baseline=matrices(properties.D1TotalInstances,properties.D2TotalInstances,properties.D1Spacing,properties.D2Spacing);
  if(properties.SkippedItemArray?.length||baseline.length!==properties.instanceTransforms.length||baseline.some((matrix,index)=>!near(matrix,properties.instanceTransforms[index])))throw new DefinitionError('孔阵列方向和原实例变换不一致');
  updated.D1TotalInstances=values['D1@'+pattern.name];updated.D2TotalInstances=values['D2@'+pattern.name];updated.D1Spacing=values['D3@'+pattern.name]/1000;updated.D2Spacing=values['D4@'+pattern.name]/1000;
  updated.instanceTransforms=matrices(updated.D1TotalInstances,updated.D2TotalInstances,updated.D1Spacing,updated.D2Spacing);
  for(const feature of configuration.features)for(const dimension of feature.dimensions??[]){const name=canonical(dimension.name);if(name==='D2@'+base.name)dimension.value=length;else if(Object.hasOwn(values,name))dimension.value=dimension.dimensionType===2?values[name]:values[name]/1000;}
  function referenceDisplacement(entity){
    const reference=entity.reference;
    if(reference?.referencePlaneTransform&&reference.feature&&original.features.some(feature=>feature.name===reference.feature&&feature.type==='RefPlane'&&feature.definition?.properties.Type===10))return [0,0,0];
    if(reference?.planeParameters){const normal=reference.planeParameters.slice(0,3),origin=reference.planeParameters.slice(3,6);if(Math.abs(normal[1])<1e-7)return [0,0,0];if(Math.abs(Math.abs(normal[1])-1)>1e-7)throw new DefinitionError('边框配合引用未解析斜面');if(Math.abs(origin[1])<1e-7)return [0,0,0];if(Math.abs(origin[1]+oldLength)<1e-7)return [0,-(length-oldLength)*1000,0];}
    if(reference?.lineParameters&&reference.start&&reference.end){const ends=[reference.start,reference.end];if(ends.every(point=>Math.abs(point[1])<1e-7))return [0,0,0];if(ends.every(point=>Math.abs(point[1]+oldLength)<1e-7))return [0,-(length-oldLength)*1000,0];if(Math.abs(Math.abs(reference.lineParameters[4])-1)<1e-7)return [0,0,0];}
    throw new DefinitionError('边框配合没有对应原端面或纵向边');
  }
  return {template,referenceDisplacement,evidence:{source:'native-axial-base-flange-midpoint-slot-and-pattern-equations',sourceLengthMm:oldLength*1000,lengthMm:length*1000,pattern:{counts:[updated.D1TotalInstances,updated.D2TotalInstances],spacingMm:[updated.D1Spacing*1000,updated.D2Spacing*1000]}}};
}

export async function deriveWallBracket(source,plan,readSource){
  if(source.source.path!==plan.templatePath||source.source.sha256!==plan.sourceSha256)throw new DefinitionError('穿墙支架源身份不一致');
  const original=one(source.configurations.filter(row=>row.name===plan.configuration),'穿墙支架原配置'),definitions=new Map(),driven=new Map(),evidence=[];
  const read=async path=>definitions.get(path)??readSource(path);
  for(const model of plan.models){
    const part=await readSource(model.templatePath);let result;
    if(model.modelId.startsWith('8GX.160.02'))result=deriveWallBracketRail(part,model);
    else{const dimension=model.dimensions[0];result=driveStraightWeldDefinition(part,model.configuration,{source:'captured-governing-assembly-equation',templatePath:model.templatePath,configuration:model.configuration,dimension:dimension.dimension,sourceValue:dimension.sourceValueMm,value:dimension.valueMm});}
    definitions.set(model.templatePath,result.template);for(const placement of plan.placements.filter(placement=>placement.modelId===model.modelId))driven.set(placement.componentId,result);evidence.push({modelId:model.modelId,evidence:result.evidence});
  }
  const displacement=entity=>{
    const chain=entity.component.split('/'),component=original.components.find(component=>component.name===chain[0]),part=component&&driven.get(component.id);if(!component)throw new DefinitionError('穿墙支架配合组件不唯一');if(!part)return [0,0,0];if(chain.length!==1)throw new DefinitionError('受驱边框配合实例链不一致');
    const placement=swMatrix(component.transform);if(part.referenceDisplacement)return vector(part.referenceDisplacement(entity),placement);
    const inverse=inverseRigidMatrix(placement),point=transformPoint(entity.parameters.slice(0,3).map(value=>value*1000),inverse),direction=vector(entity.parameters.slice(3,6),inverse);return vector(weldReferenceDisplacement(part.motion,point,direction,entity.referenceType,entity.parameters[6]*1000),placement);
  };
  const slots=original.features.filter(feature=>!feature.suppressed&&feature.mateDefinition?.type===21),channel=one(original.components.filter(component=>component.path.endsWith('/8ZX.202.520.SLDPRT')),'原槽钢');
  if(slots.length!==1||slots[0].mateDefinition.entities[0]?.component!==channel.name||!slots[0].mateDefinition.entities[1]?.component.startsWith('6ZX.204.323-2/')||slots[0].mateDefinition.entities.some(entity=>!near(displacement(entity),[0,0,0])))throw new DefinitionError('原槽口位置无法保持');
  const solving=structuredClone(original);solving.features=solving.features.filter(feature=>feature.mateDefinition?.type!==21);solving.components.find(component=>component.id===channel.id).fixedPosition=true;
  const installation=await solveAssemblyTranslations(solving,displacement),slotMembers=slots[0].mateDefinition.entities.map(entity=>original.components.find(component=>component.name===entity.component.split('/')[0]));
  if(slotMembers.some(component=>!near(installation.translations.find(row=>row.componentId===component.id).delta,[0,0,0],1e-5)))throw new DefinitionError('驱动后原槽口相对位置改变');
  installation.evidence.push({feature:slots[0].name,source:'original-unmoved-slot-and-center-hole',mode:'preserve-captured-free-slot-location',nativeSlotModeAssumed:false});
  const template=structuredClone(source),configuration=template.configurations.find(row=>row.name===plan.configuration);delete configuration.validation;configuration.features=configuration.features.filter(feature=>feature.type!=='CoordSys');for(const component of configuration.components)component.transform=shifted(component.transform,installation.translations.find(row=>row.componentId===component.id).delta);configuration.offlineAssemblySolution=installation;definitions.set(source.source.path,template);
  function referenceDisplacement(entity){
    const chain=entity.component.split('/');if(chain.length!==2)throw new DefinitionError('总装穿墙支架配合实例链不一致');const component=one(original.components.filter(component=>component.name===chain[1]),'总装边框实例'),part=driven.get(component.id);if(!part?.referenceDisplacement)throw new DefinitionError('总装穿墙支架未引用原边框面');
    const delta=vector(part.referenceDisplacement(entity),swMatrix(component.transform)),motion=installation.translations.find(row=>row.componentId===component.id).delta;return delta.map((value,axis)=>value+motion[axis]);
  }
  return {template,loadTemplate:read,definitions,referenceDisplacement,evidence:[...evidence,{installation}],source:'captured-wall-bracket-profiles-and-original-mates',nativeModelValidationRequired:false};
}
