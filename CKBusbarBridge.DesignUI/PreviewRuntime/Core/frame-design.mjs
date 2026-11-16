import {DefinitionError} from './expressions.mjs';
import {prepareStructureBusinessDrives} from './structure-business-drives.mjs';
import {planNativeEquationDrives} from './native-design-drives.mjs';
import {planCabinetBases} from './cabinet-base-planning.mjs';
import {frameTemplatePaths} from './structure-sources.mjs';
import {solveStructureEquations,reconcileStructureDimensions} from './structure-equations.mjs';
import {frameInstallationDimensions} from './structure-installation.mjs';
import {memoizeDefinition} from './definition-memo.mjs';
export {frameTemplatePaths} from './structure-sources.mjs';

const normalize=value=>Math.sign(value)*Math.round(Math.abs(value)*1000)/1000;
const finite=(value,label,positive=false)=>{if(!Number.isFinite(value)||positive&&value<=0)throw new DefinitionError(label+'必须是有限'+(positive?'正':'')+'数');return value;};
const invalidFolder=/[<>:"/\\|?*\u0000-\u001f]/g;
function sourceName(value,wall){
  if(typeof value!=='string'||!value.trim())throw new DefinitionError('基架来源名称不能为空');const name=value.trim();
  if(!wall&&(name==='.'||name==='..'||name.endsWith('.')||/[<>:"/\\|?*\u0000-\u001f]/.test(name)||/^(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)/i.test(name)))throw new DefinitionError('基架来源名称不是有效Windows目录名');
  return name;
}
function layout(input,length){
  const count=input.insulatorCount??0,positions=input.insulatorPositions??[];
  if(!Number.isInteger(count)||count<0||count>3||!Array.isArray(positions)||positions.length!==count)throw new DefinitionError('基架绝缘子数量及定位值必须匹配0至3套');
  const normalized=positions.map(position=>normalize(finite(position,'绝缘子定位')));if(normalized.some(position=>position<0||position>length)||new Set(normalized).size!==normalized.length)throw new DefinitionError('绝缘子定位超出基架长度或重复');return normalized;
}

// Pure port of the two original frame drive planners. No native file access,
// geometry equivalence checks or SolidWorks execution occurs here.
export function planFrameDesigns(scenario,inputs,context={}){
  if(!['face-to-face','face-to-wall','side-to-side'].includes(scenario)||!Array.isArray(inputs))throw new DefinitionError('基架场景或输入清单无效');
  const wall=scenario==='face-to-wall',models=new Map(),folders=new Map(),plans=[];
  for(const [index,input]of inputs.entries()){
    if(!input||typeof input!=='object')throw new DefinitionError('基架计划输入不能为空');
    const name=sourceName(input.sourceName,wall),kind=input.kind??'Regular',small=input.smallOffset??null;
    if(!(wall?['Regular','Large1','Large2']:['Regular','FaceToFaceLargeOffsetCorner']).includes(kind)||small&&(wall||kind!=='Regular'))throw new DefinitionError('基架模板类型不属于本场景');
    if(wall&&kind!=='Regular'&&input.isReversed)throw new DefinitionError('面对墙大基架不支持反装');
    finite(input.width,'基架宽度',true);
    let width=normalize(finite(small?.frameWidth??input.width,'基架宽度',true)),length=normalize(finite(input.length,'基架长度',true)),height=normalize(finite(input.height,'基架高度',true)),wallEntryDistance=0,smallOffset=null;
    const globalVariables={};
    if(wall){
      const vertical=normalize(finite(input.wallVerticalClearance??0,'上下墙距'));height=normalize(height-2*vertical);finite(height,'补偿后基架高度',true);
      if(kind==='Large1'){const horizontal=normalize(finite(input.wallHorizontalClearance??0,'左右墙距'));wallEntryDistance=normalize(finite(input.wallEntryDistance??0,'进墙距离'));width=normalize(width-2*horizontal);length=normalize(length+wallEntryDistance);finite(width,'补偿后基架宽度',true);finite(length,'补偿后基架长度',true);}
      if(length<(kind==='Regular'?200:300))throw new DefinitionError('基架长度小于原模板允许最小值');
    }
    if(small){
      for(const property of ['inletOffsetDirection','outletOffsetDirection'])if(![0,1].includes(small[property]))throw new DefinitionError('开孔偏移方向只能为0或1');
      for(const property of ['inletOffsetSize','outletOffsetSize'])if(finite(small[property],'开孔偏移尺寸')<0)throw new DefinitionError('开孔偏移尺寸不能为负');
      smallOffset={frameWidth:width,inletOffsetDirection:small.inletOffsetDirection,inletOffsetSize:normalize(small.inletOffsetSize),outletOffsetDirection:small.outletOffsetDirection,outletOffsetSize:normalize(small.outletOffsetSize)};
    }
    const positions=layout(input,length),isTemplateDefault=kind==='Regular'&&!small&&width===800&&length===1000&&height===500;
    if(!isTemplateDefault)Object.assign(globalVariables,{'基架宽度':width,'基架长度':length,'基架高度':height});
    if(smallOffset)Object.assign(globalVariables,{'进孔偏移方向':smallOffset.inletOffsetDirection,'进孔偏移尺寸':smallOffset.inletOffsetSize,'出孔偏移方向':smallOffset.outletOffsetDirection,'出孔偏移尺寸':smallOffset.outletOffsetSize});
    if(wall&&kind!=='Regular'){
      const regularWidth=normalize(finite(context.regularFrameWidth,'常规基架宽度',true));
      if(kind==='Large1'){const eccentricity=normalize(finite(context.wallCabinetEccentricity,'柜对墙偏心'));globalVariables['上侧封板宽度']=normalize((width-regularWidth)/2+eccentricity);globalVariables['下侧封板宽度']=normalize((width-regularWidth)/2-eccentricity);}
      else{const baseOpening=finite(context.baseOpeningHeight,'基架高度',true);if(baseOpening-height>.010000001)throw new DefinitionError('基架高度不能大于墙孔高度');globalVariables['侧盖板宽度']=normalize(Math.max(0,height-normalize(baseOpening)));}
    }
    const templateIdentity=input.templateIdentity??(small?'FaceToFaceSmallOffset032':kind==='FaceToFaceLargeOffsetCorner'?'FaceToFaceLargeOffsetCorner028':kind==='Large1'?'FaceToWallLarge1':kind==='Large2'?'FaceToWallLarge2':'Regular024');
    if(!Object.hasOwn(frameTemplatePaths,templateIdentity)||templateIdentity==='SideToSidePhaseChange029'&&scenario!=='side-to-side')throw new DefinitionError('基架原模板身份无效');
    const originalSupport=input.originalSupport;
    if(originalSupport){finite(originalSupport.assemblyHeight,'原规划绝缘子总高',true);finite(originalSupport.phaseSpacing,'原规划相间距',true);if(originalSupport.assemblyHeight<=25)throw new DefinitionError('原规划绝缘子缺少本体高度');for(const key of ['transverseOffsets','mounts'])if(originalSupport[key]!=null&&(!Array.isArray(originalSupport[key])||originalSupport[key].length!==positions.length))throw new DefinitionError('原逐位绝缘子计划数量与基架不一致');if(originalSupport.transverseOffsets?.some(value=>!Number.isFinite(value)))throw new DefinitionError('原绝缘子横移量必须是有限数');}
    if(input.originalDrive){
      const driven=input.originalDrive,actualDimensions={width,length,height},mismatches=[];
      if(driven.sourceName!==name)mismatches.push('来源名称');
      for(const key of Object.keys(actualDimensions))if(!Number.isFinite(driven[key])||Math.abs(driven[key]-actualDimensions[key])>.001)mismatches.push(key+'：原值'+driven[key]+'，计算值'+actualDimensions[key]);
      // JSON object member order is not part of the original drive contract.
      for(const key of new Set([...Object.keys(driven.globalVariables??{}),...Object.keys(globalVariables)]))if(!Object.hasOwn(driven.globalVariables??{},key)||!Object.hasOwn(globalVariables,key)||driven.globalVariables[key]!==globalVariables[key])mismatches.push(key+'：原值'+driven.globalVariables?.[key]+'，计算值'+globalVariables[key]);
      if(mismatches.length)throw new DefinitionError('基架尺寸或方程未与原生成规划对应（'+name+'；'+mismatches.join('；')+'）');
    }
    const folderName=wall?name.replace(invalidFolder,'_'):name,model={sourceName:name,folderName,width,length,height,insulatorCount:positions.length,insulatorPositions:positions,isTemplateDefault,globalVariables,suppressTemplateAngleNotches:input.suppressTemplateAngleNotches??null,kind,templateIdentity,templatePath:frameTemplatePaths[templateIdentity],smallOffset,wallEntryDistance,...originalSupport?{originalSupport:structuredClone(originalSupport)}:{}};
    const modelKey=name.toUpperCase(),previous=models.get(modelKey),folderKey=folderName.toUpperCase(),owner=folders.get(folderKey);
    if(!wall&&owner&&owner!==modelKey)throw new DefinitionError('不同基架名称映射到同一文件夹');folders.set(folderKey,modelKey);
    if(previous){const physical=plan=>[plan.width,plan.length,plan.height,plan.insulatorCount,plan.insulatorPositions,plan.kind,plan.templateIdentity,plan.smallOffset,plan.wallEntryDistance,plan.globalVariables,plan.suppressTemplateAngleNotches,plan.originalSupport];if(JSON.stringify(physical(previous))!==JSON.stringify(physical(model)))throw new DefinitionError('同源基架的尺寸、模板或绝缘子布置不一致');}else models.set(modelKey,model);
    const isReversed=kind!=='FaceToFaceLargeOffsetCorner'&&!!input.isReversed,suffix=isReversed?'2':'1',inlet=wall?'坐标系-进'+suffix:input.inletCoordinateName??'坐标系-进'+suffix,outlet=wall?'坐标系-出'+suffix:input.outletCoordinateName??'坐标系-出'+suffix;
    if(!wall&&!['1','2'].some(route=>inlet==='坐标系-进'+route&&outlet==='坐标系-出'+route))throw new DefinitionError('基架进出坐标系必须使用同编号原路由');
    plans.push({positionIndex:index+1,positionKey:'位置'+(index+1),sourceName:(previous??model).sourceName,model:previous??model,isReversed,inletCoordinateName:inlet,outletCoordinateName:outlet});
  }
  return {version:'original-frame-design/1',scenario,inputs:structuredClone(inputs),context:structuredClone(context),plans};
}

export async function buildFrameDesignExport(plan,read,{memo,memoOwner=read}={}){
  const definitions=[],models=new Map();
  for(const position of plan.plans){const model=position.model,key=model.sourceName.toUpperCase();if(models.has(key))continue;const design=await memoizeDefinition(memo,memoOwner,'frame-design',model,async()=>{const template=await read(model.templatePath),configuration=template.configurations.find(configuration=>configuration.name==='默认');if(template.source.path!==model.templatePath||!configuration)throw new DefinitionError('基架JSON没有对应原模板配置');const prepared=await prepareStructureBusinessDrives(template,configuration.name,model.globalVariables,read,model.smallOffset?{kind:'FaceToFaceSmallOffset032',suppressTemplateAngleNotches:model.suppressTemplateAngleNotches}:null),drivenConfiguration=prepared.template.configurations.find(row=>row.name===configuration.name),equations=await planNativeEquationDrives(prepared.template,drivenConfiguration,model.globalVariables,prepared.readTemplate),structure=await solveStructureEquations(prepared.template,configuration.name,model.globalVariables,prepared.readTemplate,{rootDimensions:frameInstallationDimensions(model)});structure.businessDrives=prepared.evidence;return {...structuredClone(model),sourceSha256:template.source.sha256,configuration:configuration.name,...equations,dimensions:reconcileStructureDimensions(structure,equations.dimensions),structure,installation:{inletCoordinateNames:['坐标系-进1','坐标系-进2'],outletCoordinateNames:['坐标系-出1','坐标系-出2']}};});models.set(key,design);definitions.push(design);}
  return {...structuredClone(plan),models:definitions,placements:plan.plans.map(position=>({positionKey:position.positionKey,modelId:position.sourceName,inletCoordinateName:position.inletCoordinateName,outletCoordinateName:position.outletCoordinateName,isReversed:position.isReversed}))};
}

export function validateProjectFrameDesign(scenario,record,frameDesign){
  if(frameDesign===undefined)return null;
  if(!frameDesign||!Array.isArray(frameDesign.inputs))throw new DefinitionError('中间基架业务组输入无效');
  let context={};
  if(scenario==='face-to-wall'&&frameDesign.inputs.length){
    const bases=planCabinetBases(scenario,record);
    // Original FaceToWallSingleBaseParameterAdapter selects the one enabled
    // base. Cabinet orientation does not change the enabled opening group.
    if(bases.plans.length!==1)throw new DefinitionError('面对墙基架必须连接唯一启用的柜前或柜后基座');
    const position=bases.plans[0],base=position.baseModel;
    context={basePositionName:position.positionName,regularFrameWidth:base.width,wallCabinetEccentricity:bases.record.values['General.WallCabinetEccentricity'],baseOpeningHeight:base.globalVariables['基座开孔高度']};
    if(frameDesign.originalContext){const original=frameDesign.originalContext;if(Object.keys(original).some(key=>!['regularFrameWidth','wallCabinetEccentricity','baseOpeningHeight'].includes(key)))throw new DefinitionError('原基架规划上下文无效');for(const key of ['regularFrameWidth','wallCabinetEccentricity','baseOpeningHeight'])finite(original[key],'原基架规划 '+key);context={...context,...original};}
  }
  const plan=planFrameDesigns(scenario,frameDesign.inputs,context),candidates=planCabinetBases(scenario,record).plans.filter(position=>scenario==='face-to-wall'||position.positionName.startsWith('B')),requested=frameDesign.basePositionName;
  if(requested!==undefined&&(typeof requested!=='string'||!candidates.some(position=>position.positionName===requested)))throw new DefinitionError('首段基架连接的基座未启用或不属于本场景');
  const selected=requested??(candidates.length===1?candidates[0].positionName:undefined);
  return {...plan,...selected?{basePositionName:selected}:{}};
}
