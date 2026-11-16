import {assignCabinetRenderRoles} from './cabinet-render-roles.mjs';
import {DefinitionError,solveEquations} from './expressions.mjs';
import {equationInitialValues} from './equation-context.mjs';
import {deriveCabinetBody} from './cabinet-body.mjs';
import {deriveCabinetOpenings,originalCabinetPartName} from './cabinet-openings.mjs';
import {mapExtrudedPlane} from './extruded-topology.mjs';
import {solveAssemblyTranslations} from './assembly-translations.mjs';
import {swMatrix,inverseRigidMatrix,multiplyMatrix,transformPoint} from './geometry.mjs';
import {cabinetScenePaths} from './cabinet-binding.mjs';
import {deriveWallCabinetScene} from './wall-cabinet-scene.mjs';
import {driveCabinetMountCoordinates} from './cabinet-mount-coordinates.mjs';
import {buildFixedCabinetCopper,cabinetRoofConductors} from './cabinet-fixed-copper.mjs';
import {cabinetOffsetDimension} from './cabinet-offset-dimensions.mjs';
export {cabinetScenePaths} from './cabinet-binding.mjs';

const canonical=name=>name.split('@').slice(0,-1).join('@');
const shifted=(transform,delta)=>{const result=[...transform];for(let axis=0;axis<3;axis++)result[9+axis]+=delta[axis]/1000;return result;};
const directionTransform=(vector,matrix)=>[0,1,2].map(axis=>matrix[axis]*vector[0]+matrix[4+axis]*vector[1]+matrix[8+axis]*vector[2]);

function localReferenceDelta(entity,body){
  const reference=entity.reference;
  if(reference?.planeParameters)return mapExtrudedPlane(reference,body.topologies).delta.map(value=>value*1000);
  if(reference?.referencePlaneTransform&&reference.feature){const definition=body.originalConfiguration.features.find(feature=>feature.name===reference.feature&&feature.type==='RefPlane');if(definition?.definition?.properties.Type===10&&!(definition.dimensions?.length))return [0,0,0];}
  throw new DefinitionError('柜体安装配合没有可求解的原实体面或固定基准面');
}

// This stage contains cabinet bodies and the original scene geometry. Cabinet
// outgoing copper belongs to the free-drawing workflow and remains explicitly
// deferred; independent bases are added at the total assembly level later.
export async function deriveCabinetScene(sourceTemplate,configurationName,scenario,record,readSource){
  if(scenario==='face-to-wall')return deriveWallCabinetScene(sourceTemplate,configurationName,record,readSource);
  if(cabinetScenePaths[scenario]!==sourceTemplate.source.path||record?.status!=='parameters-only')throw new DefinitionError('柜体场景驱动未对应当前原项目参数');
  const values=record.values,source=sourceTemplate.configurations.find(configuration=>configuration.name===configurationName);
  if(!source?.components.length)throw new DefinitionError('柜体场景缺少原装配组件');
  const definitions=new Map(),children=new Map(),evidence=[],diagnostics=[];
  const read=async path=>{if(!definitions.has(path))definitions.set(path,await readSource(path));return definitions.get(path);};
  for(const component of source.components){
    if(!component.path?.toLowerCase().endsWith('.sldasm'))continue;
    const childTemplate=await read(component.path),childSource=childTemplate.configurations.find(configuration=>configuration.name===component.configuration);
    const bodies=childSource?.components.filter(candidate=>originalCabinetPartName(candidate.path));
    if(bodies?.length!==1)throw new DefinitionError('原柜体子装配体没有唯一柜体：'+component.name);
    const bodyComponent=bodies[0],prefix=originalCabinetPartName(bodyComponent.path)==='A柜'?'ACabinet':'BCabinet',bodySource=await read(bodyComponent.path),bodyName=bodyComponent.configuration;
    const driven=deriveCabinetBody(bodySource,bodyName,{height:values[prefix+'.CabinetHeight'],depth:values[prefix+'.CabinetDepth'],width:values[prefix+'.CabinetWidth']});
    const hole=side=>{const path=prefix+'.'+side;return {enabled:values[path+'.IsEnabled'],edgeDistance:values[path+'.ToCabinetEdgeDistance'],fixedSpacing:values[path+'.FixedHoleSpacing'],l1:values[path+'.L1'],l2:values[path+'.L2'],l3:values[path+'.L3']};};
    const opened=deriveCabinetOpenings(driven.template,bodyName,{mode:scenario==='side-to-side'&&values['General.CabinetTopOpeningMode']==='LeftRight'?'left-right':'front-back',front:hole('FrontHole'),back:hole('BackHole')});
    definitions.set(bodyComponent.path,opened.template);
    const body={...driven,originalConfiguration:bodySource.configurations.find(configuration=>configuration.name===bodyName)};
    const selectedMates=childSource.features.filter(feature=>!feature.suppressed&&feature.mateDefinition&&feature.mateDefinition.entities.some(entity=>entity.component===bodyComponent.name)&&feature.mateDefinition.entities.every(entity=>entity.component===bodyComponent.name||entity.componentId===-1&&entity.reference));
    const bodySystem={...childSource,components:[bodyComponent],features:selectedMates};
    const bodyRotation=swMatrix(bodyComponent.transform),installation=await solveAssemblyTranslations(bodySystem,entity=>directionTransform(localReferenceDelta(entity,body),bodyRotation),{centeredWidthMates:selectedMates.filter(feature=>feature.mateDefinition.type===11).map(feature=>feature.name)});
    const translation=installation.translations.find(row=>row.componentId===bodyComponent.id),updatedTransform=shifted(bodyComponent.transform,translation.delta),child=structuredClone(childTemplate),configuration=child.configurations.find(candidate=>candidate.name===component.configuration);
    delete configuration.validation;
    configuration.components.find(candidate=>candidate.id===bodyComponent.id).transform=updatedTransform;
    for(const candidate of configuration.components.filter(candidate=>candidate.id!==bodyComponent.id)){candidate.representation={mode:'deferred',reason:'柜内伸出铜排按原参数进入自由绘制工作流，尚未生成'};diagnostics.push({component:candidate.name,message:candidate.representation.reason});}
    for(const feature of configuration.features.filter(feature=>feature.mateDefinition?.entities.some(entity=>!entity.reference)))diagnostics.push({assembly:component.name,feature:feature.name,message:'原配合缺失引用，不用于当前柜体定位'});
    configuration.features=configuration.features.filter(feature=>feature.type!=='CoordSys');configuration.offlineAssemblySolution=installation;
    definitions.set(component.path,child);children.set(component.id,{component,bodyComponent,body,prefix,originalTransform:bodyComponent.transform,updatedTransform,childSource});
    evidence.push({component:component.name,body:bodyComponent.name,prefix,bodyDrive:driven.evidence,holes:opened.evidence,installation});
  }
  if(children.size!==2)throw new DefinitionError('两柜场景缺少 A/B 柜业务组');
  const eccentricity=values['General.CabinetEccentricity'],globals={'柜间距A':values['General.CabinetSpacing'],'楼高D':values['General.FloorHeight'],'B':values['General.ParameterB'],'柜间偏心值':Math.abs(eccentricity),'偏移方向':eccentricity>0?1:0};
  if(Object.values(globals).some(value=>!Number.isFinite(value)))throw new DefinitionError('柜体场景原整体参数不是有限数');
  for(const global of Object.keys(globals))if(source.equations.filter(equation=>equation.globalVariable&&new RegExp('^\\s*"'+global+'"\\s*=').test(equation.expression)).length!==1)throw new DefinitionError('柜体整体原全局变量缺失：'+global);
  const context=await equationInitialValues(sourceTemplate,source,read),solved=solveEquations(source.equations,context.initialValues,globals),dimensionValues={};
  for(const feature of source.features.filter(feature=>!feature.suppressed&&feature.mateDefinition?.type===5))for(const dimension of feature.dimensions.filter(dimension=>dimension.dimensionType===0)){const name=canonical(dimension.name);if(Object.hasOwn(solved,name))dimensionValues[name]=solved[name];}
  dimensionValues[scenario==='side-to-side'?'I@距离7':'I@距离6']=values['ACabinet.GroundHeight'];dimensionValues[scenario==='side-to-side'?'I@距离6':'I@距离5']=values['BCabinet.GroundHeight'];
  const active=source.features.filter(feature=>!feature.suppressed&&feature.mateDefinition),invalid=active.filter(feature=>feature.mateDefinition.entities.some(entity=>!entity.reference));
  for(const feature of invalid)diagnostics.push({assembly:sourceTemplate.source.path,feature:feature.name,message:'原配合缺失引用，不用于当前柜体定位'});
  const rootSystem={...source,features:source.features.filter(feature=>!invalid.includes(feature))};
  const geometryDisplacement=entity=>{
    const firstName=entity.component.split('/')[0],component=source.components.find(candidate=>candidate.name.split('/').at(-1)===firstName),child=children.get(component?.id);
    if(!child)return [0,0,0];
    const chain=entity.component.split('/');
    if(chain.length===1){const datum=child.childSource.features.find(feature=>feature.name===entity.reference?.feature&&feature.type==='RefPlane');if(datum?.definition?.properties.Type===10&&!(datum.dimensions?.length))return [0,0,0];throw new DefinitionError('柜体子装配体根基准面尚未求解');}
    if(chain.length!==2||chain[1]!==child.bodyComponent.name.split('/').at(-1))throw new DefinitionError('柜体总装配合未对应原柜体实例链');
    const originalPlacement=multiplyMatrix(swMatrix(component.transform),swMatrix(child.originalTransform)),updatedPlacement=multiplyMatrix(swMatrix(component.transform),swMatrix(child.updatedTransform)),point=entity.parameters.slice(0,3).map(value=>value*1000),local=transformPoint(point,inverseRigidMatrix(originalPlacement)),delta=localReferenceDelta(entity,child.body),updated=transformPoint(local.map((value,index)=>value+delta[index]),updatedPlacement);
    return updated.map((value,index)=>value-point[index]);
  };
  const installation=await solveAssemblyTranslations(rootSystem,geometryDisplacement,{dimensionValues,centeredWidthMates:active.filter(feature=>feature.mateDefinition.type===11&&!invalid.includes(feature)).map(feature=>feature.name)});
  const template=structuredClone(sourceTemplate),configuration=template.configurations.find(candidate=>candidate.name===configurationName);delete configuration.validation;assignCabinetRenderRoles(configuration,scenario);
  for(const component of configuration.components){const translation=installation.translations.find(candidate=>candidate.componentId===component.id);component.transform=shifted(component.transform,translation.delta);}
  for(const equation of configuration.equations){const name=/^\s*"([^"]+)"\s*=/.exec(equation.expression)?.[1];if(equation.globalVariable&&Object.hasOwn(globals,name))equation.expression='"'+name+'" = '+globals[name];}
  for(const feature of configuration.features)for(const dimension of feature.dimensions??[])if(Object.hasOwn(dimensionValues,canonical(dimension.name)))dimension.value=dimensionValues[canonical(dimension.name)]/1000;
  const mountCoordinates=driveCabinetMountCoordinates(source,configuration,[...children.values()].map(child=>({rootComponent:child.component,originalBodyTransform:child.originalTransform,updatedBodyTransform:child.updatedTransform,original:child.body.originalConfiguration,derived:definitions.get(child.bodyComponent.path).configurations.find(row=>row.name===child.bodyComponent.configuration)})));configuration.offlineAssemblySolution=installation;
  evidence.push({kind:'cabinet-mount-coordinates',...mountCoordinates});
  for(const coordinate of mountCoordinates.pending)diagnostics.push({coordinate,message:'柜内伸出排接口控制草图待求解，未返回旧坐标'});
  definitions.set(sourceTemplate.source.path,template);
  const cabinetComponentChains=new Map([...children].map(([id,child])=>[id,[child.component.name,child.bodyComponent.name]]));
  const parameters=[await cabinetOffsetDimension({source,scenario,value:eccentricity,globals,initialValues:context.initialValues,solved,installation,geometryDisplacement,cabinetComponentChains})];
  for(const [id,child]of children){const component=configuration.components.find(candidate=>candidate.id===id),placement=multiplyMatrix(swMatrix(component.transform),swMatrix(child.updatedTransform)),base=child.body.topologies[0];
    for(const [suffix,axis,label]of [['CabinetDepth',0,'柜深'],['CabinetHeight',1,'柜高'],['CabinetWidth',2,'柜宽']]){const name=child.prefix+'.'+suffix,value=values[name],edge=base.edges.map(pair=>pair.target).find(edge=>Math.abs(Math.abs(edge.end[axis]-edge.start[axis])*1000-value)<1e-5&&[0,1,2].filter(index=>index!==axis).every(index=>Math.abs(edge.end[index]-edge.start[index])<1e-8));if(!edge)throw new DefinitionError('柜体业务尺寸缺少实际原轮廓见证边');parameters.push({name,label:(child.prefix==='ACabinet'?'A柜':'B柜')+label,value,editable:true,annotationComponentChain:cabinetComponentChains.get(id),annotationEdges:[[transformPoint(edge.start.map(value=>value*1000),placement),transformPoint(edge.end.map(value=>value*1000),placement)]],source:'native-profile-incidence-bound-to-original-ui-field'});}
  }
  const fixedCopper=values['General.BusbarSpec']?await buildFixedCabinetCopper({scenario,record,children:[...children.values()],rootConfiguration:configuration,read}):null;
  const roofConductors=new Map();
  if(fixedCopper)for(const [id,child] of children){
    const component=configuration.components.find(candidate=>candidate.id===id);
    roofConductors.set(child.bodyComponent.path,cabinetRoofConductors(fixedCopper,component.name,
      multiplyMatrix(swMatrix(component.transform),swMatrix(child.updatedTransform))));
  }
  return {template,loadTemplate:read,parameters,fixedCopper,roofConductors,evidence:[...evidence,{rootInstallation:installation,globals,dimensionValues}],diagnostics,source:'original-cabinet-part-sketches-and-captured-mate-layout',nativeGeometryEquivalenceVerified:false};
}
