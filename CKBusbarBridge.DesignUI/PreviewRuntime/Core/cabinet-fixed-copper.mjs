import {DefinitionError} from './expressions.mjs';
import {swMatrix,inverseRigidMatrix,multiplyMatrix,transformPoint,solidToMesh} from './geometry.mjs';
import {solveAssemblyTranslations} from './assembly-translations.mjs';
import {mapExtrudedPlane} from './extruded-topology.mjs';
import {openSheetBoundary} from './open-sheet.mjs';
import {freeCopperVersion,planFreeCopper,evaluateFreeCopper} from './free-copper.mjs';

const add=(a,b)=>a.map((v,i)=>v+b[i]),sub=(a,b)=>a.map((v,i)=>v-b[i]),scale=(a,s)=>a.map(v=>v*s),dot=(a,b)=>a.reduce((v,x,i)=>v+x*b[i],0),unit=a=>scale(a,1/Math.hypot(...a));
const direction=(vector,matrix)=>sub(transformPoint(vector,matrix),transformPoint([0,0,0],matrix));
const cross=(a,b)=>[a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0]];
const canonical=name=>name.split('@').slice(0,-1).join('@');

export function cabinetRoofConductors(fixedCopper,rootComponentName,bodyPose){
  const inverse=inverseRigidMatrix(bodyPose);
  return (fixedCopper?.bars??[]).filter(bar=>bar.source.componentChain[0]===rootComponentName).map(bar=>{
    const {data,segments}=planFreeCopper(bar.freeCopper),segment=segments[0];
    if(segments.length!==1)throw new DefinitionError('柜顶让位口需要原伸出排的直段截面');
    const width=direction(segment.width,inverse),thickness=direction(segment.thickness,inverse);
    return {points:data.points.map(point=>transformPoint(point,inverse)),
      halfExtent:width.map((value,axis)=>(Math.abs(value)*data.widthMm+Math.abs(thickness[axis])*data.thicknessMm)/2)};
  });
}

// The cabinet's outgoing bars are fixed design products. The captured shape
// sketch supplies their drawing line; original cabinet mates locate the group.
// No manually entered route or new copper template is required.
export async function buildFixedCabinetCopper({scenario,record,children,rootConfiguration,read}){
  const values=record.values,spec=String(values['General.BusbarSpec']??'').match(/^(\d+(?:\.\d+)?)\s*[×xX*]\s*(\d+(?:\.\d+)?)$/);
  if(!spec)throw new DefinitionError('柜顶伸出排规格无效');
  const width=Number(spec[1]),thickness=Number(spec[2]),rootCount=values['General.SinglePhaseRootCount'],spacing=values['General.'+(scenario==='face-to-face'?'PhaseSpacing':'ExposedBusbarPhaseSpacing')];
  if(!(width>0&&thickness>0&&spacing>0)||!Number.isInteger(rootCount)||rootCount<2||rootCount>4)throw new DefinitionError('伸出排阵列数量E必须为1到3的整数，对应设计及生成根数N为2到4；不支持无伸出排接口。');
  // Original BridgeBusbarRootCounts: existing cabinet extensions E=N-1;
  // bridge-generated members use N. These are different original identities.
  const extensionCount=rootCount-1;
  const bars=[],groups=[];
  for(const child of children){
    const {childSource,bodyComponent,body,prefix,component}=child,rootComponent=rootConfiguration.components.find(row=>row.id===component.id),rotation=swMatrix(bodyComponent.transform);
    const enabled=childSource.components.filter(row=>/柜内伸出排.*\.sldasm$/i.test(row.path)&&values[prefix+'.'+(row.name.includes('柜前')?'FrontHole':'BackHole')+'.IsEnabled']);
    const bodyMates=childSource.features.filter(row=>!row.suppressed&&row.mateDefinition&&row.mateDefinition.entities.some(entity=>entity.component===bodyComponent.name)&&row.mateDefinition.entities.every(entity=>entity.component===bodyComponent.name||entity.componentId===-1&&entity.reference));
    const copperMates=childSource.features.filter(row=>!row.suppressed&&row.mateDefinition&&row.mateDefinition.entities.some(entity=>enabled.some(outer=>entity.component===outer.name||entity.component.startsWith(outer.name+'/')))&&row.mateDefinition.entities.every(entity=>entity.reference));
    const dimensionValues={};
    for(const outer of enabled){const hole=prefix+'.'+(outer.name.includes('柜前')?'FrontHole':'BackHole'),matches=copperMates.filter(feature=>feature.mateDefinition.entities.some(entity=>entity.component===outer.name)&&feature.dimensions.some(dimension=>/铜排中心.*距离@/.test(dimension.name)));if(matches.length!==1)throw new DefinitionError('伸出排中心未对应唯一原配合：'+outer.name);for(const dimension of matches[0].dimensions.filter(row=>row.dimensionType===0))dimensionValues[canonical(dimension.name)]=values[hole+'.BusbarCenterToCabinetEdgeDistance'];}
    const installation=await solveAssemblyTranslations({...childSource,components:[bodyComponent,...enabled],features:[...bodyMates,...copperMates]},entity=>{
      if(entity.component!==bodyComponent.name)return [0,0,0];
      if(entity.reference?.planeParameters)return direction(mapExtrudedPlane(entity.reference,body.topologies).delta.map(v=>v*1000),rotation);
      if(entity.reference?.referencePlaneTransform)return [0,0,0];
      throw new DefinitionError('伸出排柜体配合缺少原平面定义');
    },{dimensionValues,centeredWidthMates:bodyMates.filter(row=>row.mateDefinition.type===11).map(row=>row.name)});
    for(const outer of enabled){
      const source=await read(outer.path),configuration=source.configurations.find(row=>row.name===outer.configuration),pattern=configuration.features.find(feature=>feature.type==='LocalLPattern'),seeds=pattern?.definition?.properties.SeedComponentArray;
      if(seeds?.length!==3)throw new DefinitionError('伸出排缺少三相原始阵列源：'+outer.name);
      const members=seeds.map(seed=>{const component=configuration.components.find(row=>row.name===seed.feature);if(!component)throw new DefinitionError('伸出排阵列源引用不存在');const phase=/柜内伸出排-([ABC])-/.exec(component.name)?.[1];if(!phase)throw new DefinitionError('伸出排原相别不明确');return {component,phase};});
      const middle=members.find(row=>row.phase==='B'),first=members.find(row=>row.phase==='A'),last=members.find(row=>row.phase==='C');if(!middle||!first||!last)throw new DefinitionError('伸出排阵列源相别重复');
      const phaseAxis=unit(sub(swMatrix(first.component.transform).slice(12,15),swMatrix(last.component.transform).slice(12,15))),stackAxis=unit(direction(pattern.definition.properties.D1Axis.lineParameters.slice(3,6),swMatrix(middle.component.transform)));
      const shift=installation.translations.find(row=>row.componentId===outer.id).delta,outerMatrix=swMatrix(outer.transform);for(let axis=0;axis<3;axis++)outerMatrix[12+axis]+=shift[axis];const placement=multiplyMatrix(swMatrix(rootComponent.transform),outerMatrix);
      const side=outer.name.includes('柜前')?'前':'后',positionName=(prefix==='Cabinet'?'':prefix==='ACabinet'?'A':'B')+'柜'+side,sequence=prefix==='Cabinet'?null:values['General.'+prefix+'ExposedBusbarPhaseSequence'];
      const defaultSequence=prefix==='ACabinet'?'CBA':'ABC',swap=sequence&&sequence!==defaultSequence;
      for(const {component:seed,phase:sourcePhase}of members){
        const leaf=await read(seed.path),leafConfiguration=leaf.configurations.find(row=>row.name===seed.configuration),shape=leafConfiguration.features.find(row=>row.name==='形状草图'),flange=leafConfiguration.features.find(row=>row.type==='SMBaseFlange');
        if(!shape?.sketch||!flange||shape.sketch.segments.filter(row=>!row.construction).length!==1)throw new DefinitionError('固定伸出排不是原直线形状：'+seed.path);
        const boundary=openSheetBoundary(shape.sketch,{...flange.definition.properties,Thickness:thickness/1000},24),profile=inverseRigidMatrix(swMatrix(shape.sketch.modelToSketch)),seedMatrix=swMatrix(seed.transform);
        if(boundary.length!==4)throw new DefinitionError('固定伸出排没有四边截面');
        const localPoints=[scale(add(boundary[0],boundary[3]),.5),scale(add(boundary[1],boundary[2]),.5)].map(point=>transformPoint([...point,0],profile)),points=localPoints.map(point=>transformPoint(point,seedMatrix));
        const origin=swMatrix(middle.component.transform).slice(12,15),phaseOffset=sourcePhase==='A'?spacing:sourcePhase==='C'?-spacing:0,midpoint=scale(add(points[0],points[1]),.5),phaseCorrection=phaseOffset-dot(sub(midpoint,origin),phaseAxis),stackCorrection=-dot(sub(midpoint,[0,0,0]),stackAxis);
        const baseline=shape.sketch.segments.find(row=>!row.construction).geometry,baselinePoints=[baseline.start,baseline.end].map(point=>transformPoint(point.map(value=>value*1000),profile)),baselineMiddle=scale(add(...baselinePoints),.5),sectionMiddle=scale(add(...localPoints),.5),localThickness=unit(sub(sectionMiddle,baselineMiddle)),baselineDirection=unit(sub(baselinePoints[1],baselinePoints[0]));
        if(Math.abs(dot(localThickness,baselineDirection))>1e-6)throw new DefinitionError('伸出排材料侧未垂直于原形状草图');
        const thicknessDirection=unit(direction(direction(localThickness,seedMatrix),placement)),nativeWidthDirection=unit(direction(direction(scale(cross(localThickness,baselineDirection),-1),seedMatrix),placement));
        const phase=swap?(sourcePhase==='A'?'C':sourcePhase==='C'?'A':sourcePhase):sourcePhase,widthDirection=unit(direction(direction(profile.slice(8,11),seedMatrix),placement));
        for(let memberIndex=0;memberIndex<extensionCount;memberIndex++){
          const correction=add(scale(phaseAxis,phaseCorrection),scale(stackAxis,stackCorrection+(2*memberIndex-extensionCount+1)*thickness)),worldPoints=points.map(point=>transformPoint(add(point,correction),placement));
          const freeCopper={version:freeCopperVersion,points:worldPoints,widthDirection,widthMm:width,thicknessMm:thickness,flatRadiusMm:flange.definition.properties.BendRadius*1000,edgeRadiusMm:flange.definition.properties.BendRadius*1000,holes:[]};planFreeCopper(freeCopper);
          const orderedPoints=baselinePoints.map(point=>transformPoint(add(transformPoint(add(point,scale(localThickness,thickness/2)),seedMatrix),correction),placement));
          const upper=worldPoints[0][1]>worldPoints[1][1]?worldPoints[0]:worldPoints[1];bars.push({id:positionName+':'+phase+':'+memberIndex,positionName,phase,memberIndex,freeCopper,previewTransform:placement,originalGeometry:{pointList:orderedPoints,startBusbarVector:thicknessDirection,widthVector:nativeWidthDirection,source:'original baseline order / inward material side / CabinetTopExtensionBusbarReader'},interfacePoint:upper,source:{path:seed.path,configuration:seed.configuration,sha256:leaf.source.sha256,componentChain:[rootComponent.name,outer.name,seed.name],bodyIndex:memberIndex}});
        }
      }
      groups.push({positionName,sourcePath:outer.path,rootCount,extensionCount,phaseSpacing:spacing,sequence,installation});
    }
  }
  return {version:'original-fixed-cabinet-copper/1',bars,groups};
}

export function appendFixedCabinetCopper(kernel,result,definition,settings){
  if(!definition)return result;
  const sources=result.solidSources??result.solids.map((_,bodyIndex)=>({componentChain:[],bodyIndex}));
  for(const bar of definition.bars){
    const evaluated=evaluateFreeCopper(kernel,{freeCopper:bar.freeCopper,parameters:{}},settings);
    result.solids.push(...evaluated.solids);
    for(const solid of evaluated.solids){
      const source={...bar.source,fixedCopperId:bar.id,phase:bar.phase,previewRole:'fixed-copper'};
      if(settings.captureLocalMeshes){
        // Factor the already verified cabinet installation; business interfaces remain in root coordinates.
        const inverse=inverseRigidMatrix(bar.previewTransform),local=solid.transform(inverse);
        try{source.localMesh=solidToMesh(local);}finally{local.delete();}
        source.transform=bar.previewTransform;
        source.shapeInputSignature=JSON.stringify({...bar.freeCopper,points:bar.freeCopper.points.map(point=>transformPoint(point,inverse)),widthDirection:direction(bar.freeCopper.widthDirection,inverse)});
      }
      sources.push(source);
    }
    for(const key of ['otherFeatureMs','cutFeatureMs'])result.geometryTimings[key]=(result.geometryTimings[key]??0)+evaluated.geometryTimings[key];
  }
  result.solidSources=sources;result.deferred=(result.deferred??[]).filter(row=>!/柜内伸出排/.test(row.path??row.component??''));return result;
}
