import {DefinitionError} from './expressions.mjs';
import {axisSketchDrive} from './axis-sketch-drive.mjs';
import {driveCompleteSketchRegions} from './driven-sketch-regions.mjs';

const sourceParts=new Map([
  ['模板/面对面模板/柜体/A柜体.SLDPRT','A柜'],['模板/面对面模板/柜体/B柜体.SLDPRT','B柜'],
  ['模板/侧对侧模板/柜体场景零件/A柜体.SLDPRT','A柜'],['模板/侧对侧模板/柜体场景零件/B柜体.SLDPRT','B柜'],
  ['模板/面对墙模板(柜前向墙)/柜体场景示意/柜体.SLDPRT','柜'],['模板/面对墙模板(柜后向墙)/柜体场景示意/柜体.SLDPRT','柜']
]);
const canonical=name=>name.split('@').slice(0,-1).join('@');
const dimensions=['edgeDistance','fixedSpacing','l1','l2','l3'];
export const originalCabinetPartName=path=>sourceParts.get(path)??null;

export function cabinetHoleModelContract(isFront,leftRight=false){
  const sketchName=leftRight?(isFront?'草图9':'草图12'):(isFront?'草图6':'草图8');
  return {sketchName,featurePositionName:'柜'+(isFront?'前':'后')+(leftRight?'左右开孔':''),edgeDimensionName:(isFront||leftRight?'距柜前距离':'距柜后距离')+'@'+sketchName,fixedHoleSpacingDimensionName:'接口固定孔间距@'+sketchName,l1DimensionName:'L1@'+sketchName,l2DimensionName:'L2@'+sketchName,l3DimensionName:'L3@'+sketchName,assemblyDimensionName:isFront?'铜排中心至柜前距离@距离3':'铜排中心距柜后距离@距离4'};
}

// Original parameter contracts remain unchanged. Only the two documented native
// naming differences are resolved here, as explicitly approved on 2026-11-03.
export function cabinetOpeningDimensionMap(configuration,isFront,leftRight=false){
  const contract=cabinetHoleModelContract(isFront,leftRight),profile=configuration.features.find(feature=>feature.name===contract.sketchName);
  if(!profile?.sketch)throw new DefinitionError('柜体开孔缺少原始草图：'+contract.sketchName);
  const names=new Set(profile.dimensions.map(dimension=>canonical(dimension.name))),aliases=[];
  const resolve=(expected,approvedActual)=>{if(names.has(expected))return expected;if(approvedActual&&names.has(approvedActual)){aliases.push({expected,actual:approvedActual,approval:'user-20261103-native-dimension-name-mapping'});return approvedActual;}throw new DefinitionError('原柜体开孔尺寸不存在：'+expected);};
  return {contract,profile,aliases,map:{edgeDistance:resolve(contract.edgeDimensionName,!isFront&&leftRight?'距柜后距离@'+contract.sketchName:null),fixedSpacing:resolve(contract.fixedHoleSpacingDimensionName,!isFront&&!leftRight?'D3@'+contract.sketchName:null),l1:resolve(contract.l1DimensionName),l2:resolve(contract.l2DimensionName),l3:resolve(contract.l3DimensionName)}};
}

export function deriveCabinetOpenings(sourceTemplate,configurationName,specification){
  const cabinetName=sourceParts.get(sourceTemplate.source.path),side=sourceTemplate.source.path.includes('/侧对侧模板/');
  if(!cabinetName)throw new DefinitionError('柜体开孔驱动未对应本项目原业务模板');
  if(!specification||Object.keys(specification).some(key=>!['mode','front','back'].includes(key))||!['front-back','left-right'].includes(specification.mode)||!side&&specification.mode!=='front-back')throw new DefinitionError('柜体开孔模式无效');
  const original=sourceTemplate.configurations.find(configuration=>configuration.name===configurationName);
  if(!original||original.components.length)throw new DefinitionError('柜体开孔源配置不是零件');
  const template=structuredClone(sourceTemplate),configuration=template.configurations.find(row=>row.name===configurationName),evidence=[],leftRight=specification.mode==='left-right';
  delete configuration.validation;
  for(const mode of side?[false,true]:[false])for(const isFront of [true,false]){
    const contract=cabinetHoleModelContract(isFront,mode),name=cabinetName+'顶开孔（'+contract.featurePositionName+'）',features=configuration.features.filter(feature=>feature.name===name&&feature.type==='Cut'),input=specification[isFront?'front':'back'];
    if(features.length!==1||typeof input?.enabled!=='boolean'||Object.keys(input).some(key=>!['enabled',...dimensions].includes(key)))throw new DefinitionError('柜体开孔组未对应唯一原特征：'+name);
    const feature=features[0],enabled=mode===leftRight&&input.enabled;feature.suppressed=!enabled;
    if(!enabled){evidence.push({feature:name,enabled:false});continue;}
    for(const key of dimensions)if(!Number.isFinite(input[key])||input[key]<=0)throw new DefinitionError('柜体开孔尺寸必须是正有限数：'+key);
    const mapping=cabinetOpeningDimensionMap(original,isFront,mode),drive=axisSketchDrive(original,contract.sketchName),values=Object.fromEntries(dimensions.map(key=>[mapping.map[key],input[key]])),solved=drive.solve(values),profile=configuration.features.find(row=>row.name===contract.sketchName);
    const selection=driveCompleteSketchRegions(mapping.profile.sketch,solved.sketch,feature.definition.properties.Contours);
    feature.definition.properties.Contours=selection.selections;profile.sketch=solved.sketch;profile.suppressed=false;
    for(const owner of configuration.features)for(const dimension of owner.dimensions??[])if(Object.hasOwn(values,canonical(dimension.name)))dimension.value=values[canonical(dimension.name)]/1000;
    for(const relation of profile.sketch.relations)if(relation.dimension&&Object.hasOwn(values,canonical(relation.dimension.name)))relation.dimension.value=values[canonical(relation.dimension.name)]/1000;
    evidence.push({feature:name,enabled:true,sketch:contract.sketchName,values,aliases:mapping.aliases,equationCount:solved.equationCount,variableCount:solved.variableCount,freeConstructionCoordinates:solved.freeConstructionCoordinates,selectionProof:selection.evidence});
  }
  for(const feature of configuration.features.filter(feature=>['左右开孔','前后开孔'].includes(feature.name))){if(!feature.type.endsWith('Folder'))throw new DefinitionError('柜体开孔模式节点不是原特征组');feature.suppressed=feature.name==='左右开孔'?!leftRight:leftRight;}
  return {template,evidence,source:'original-cabinet-hole-contract-and-native-sketch-system',upstreamBodyUnchanged:true,nativeGeometryEquivalenceVerified:false};
}
