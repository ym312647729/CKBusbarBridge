import {DefinitionError} from './expressions.mjs';
import {baseInsulatorInput,buildUniversalInsulatorDesign} from './insulator-design.mjs';
import {frameInsulatorInput,frameInsulatorCoordinate} from './structure-support-binding.mjs';
import {orderedFrameInstallationPlans} from './structure-preview-placement.mjs';
const one=(rows,message)=>{if(rows.length!==1)throw new DefinitionError(message);return rows[0];};

export function baseInstallationDimensions(model){return Object.fromEntries(model.insulators.map(placement=>[placement.placementDimensionName,placement.placementHeight]));}
export function frameInstallationDimensions(model){return Object.fromEntries(model.insulatorPositions.flatMap((position,index)=>{const mount=model.originalSupport?.mounts?.[index];return [['D'+(index+1)+'@草图3',position],...mount&&!(mount.IsFloorStanding??false)?[['D'+(index+1)+'@绝缘子吊装控制草图',position]]:[]];}));}

// Original FaceToFaceBaseAssembler / SideToSideBaseAssembler and the shared
// B-to-A frame assembler. Endpoints identify driven coordinates by name and
// native feature ID. Captured transforms are deliberately not execution poses.
export async function buildStructureInstallation({scenario,inputs,cabinet,bases,frames},read){
  const instances=[{instanceId:'cabinet-scene',modelId:'cabinet-scene',templatePath:cabinet.templatePath,configuration:cabinet.configuration}],connections=[],diagnostics=[];
  const definitions=new Map(),models=new Map([['cabinet-scene',cabinet],...bases.models.map(model=>[model.modelId,model])]);
  const coordinate=async(instanceId,name)=>{
    const instance=one(instances.filter(row=>row.instanceId===instanceId),'安装宿主实例不存在：'+instanceId),model=instance.modelId==='cabinet-scene'?cabinet:models.get(instance.modelId),path=model.templatePath;
    if(!definitions.has(path))definitions.set(path,await read(path));
    const configuration=definitions.get(path).configurations.find(row=>row.name===instance.configuration),features=configuration.features.filter(row=>row.type==='CoordSys'&&row.name===name&&!row.suppressed);
    if(!features.length&&['universal-insulator-design','mounted-insulator-design'].includes(model.kind)&&model.installation.coordinateName===name)return {instanceId,coordinateName:name,templatePath:path,configuration:instance.configuration,sourceSha256:model.sourceSha256,creationDefinition:structuredClone(model.installation),source:'original-universal-insulator-created-coordinate'};
    const feature=one(features,'原安装坐标系未唯一对应：'+name);
    return {instanceId,coordinateName:name,featureId:feature.id,templatePath:path,configuration:instance.configuration,sourceSha256:model.sourceSha256,poseEvaluation:'after-business-dimensions-and-native-mates'};
  };
  const connect=async(parentId,parentName,childId,childName,source)=>connections.push({kind:'coordinate-coincidence',parent:await coordinate(parentId,parentName),child:await coordinate(childId,childName),alignAxes:true,source});
  for(const position of bases.plans){
    const selected=bases.models.filter(model=>model.positionName===position.positionName),ordinary=one(selected.filter(model=>model.kind!=='ElevatedBase'),'基座没有唯一主体'),elevated=selected.find(model=>model.kind==='ElevatedBase');
    for(const model of selected)instances.push({instanceId:model.modelId,modelId:model.modelId,templatePath:model.templatePath,configuration:model.configuration});
    const side=scenario==='side-to-side',mode=inputs.values['General.CabinetTopOpeningMode']==='LeftRight'?'左右开孔':'前后开孔',rootName=side?position.positionName[0]+'柜-'+(position.positionName.endsWith('前')?'前':'后')+'（'+mode+'）':position.rootCoordinateName;
    if(elevated){await connect('cabinet-scene',rootName,elevated.modelId,'下','original-base-elevated-root-route');await connect(elevated.modelId,'上',ordinary.modelId,side?position.baseInstallationCoordinateName:'安装坐标系','original-base-stack-route');}
    else await connect('cabinet-scene',rootName,ordinary.modelId,side?position.baseInstallationCoordinateName:'安装坐标系','original-base-root-route');
    for(const host of selected)for(const placement of host.insulators){
      const design=placement.design;models.set(design.modelId,design);instances.push({instanceId:design.modelId,modelId:design.modelId,templatePath:design.templatePath,configuration:design.configuration,scope:'base-insulator-group'});
      await connect(host.modelId,placement.hostCoordinateName??'绝缘子安装坐标系',design.modelId,placement.childCoordinateName??'安装坐标系','original-base-insulator-route');
    }
  }
  const frameModels=[];
  if(frames?.plans.length){
    const candidates=bases.plans.filter(position=>scenario==='face-to-wall'||position.positionName.startsWith('B')),requested=frames.basePositionName??frames.context.basePositionName,anchor=requested?candidates.find(position=>position.positionName===requested):candidates.length===1?candidates[0]:null;
    if(requested&&!anchor)throw new DefinitionError('首段基架选择的基座当前未启用：'+requested);
    if(!anchor)diagnostics.push({scope:'frame-chain',message:candidates.length?'请选择首段基架连接的B柜基座':'首段基架没有启用的起始基座'});
    let parent=anchor?bases.models.find(model=>model.positionName===anchor.positionName&&model.kind!=='ElevatedBase').modelId:null,parentCoordinate='基座安装坐标系',previousOriginalPort=null;
    for(const position of orderedFrameInstallationPlans(frames)){
      const model=one(frames.models.filter(row=>row.sourceName===position.sourceName),'基架实例没有对应共享模型'),modelId='frame-model:'+model.sourceName,instanceId='frame:'+position.positionKey;
      models.set(modelId,model);instances.push({instanceId,modelId,templatePath:model.templatePath,configuration:model.configuration,positionKey:position.positionKey});
      const originalInput=frames.inputs[position.positionIndex-1];
      if(parent){await connect(parent,parentCoordinate,instanceId,position.inletCoordinateName,'original-frame-chain-route');if(previousOriginalPort&&originalInput?.originalPreviewPorts&&Math.hypot(...previousOriginalPort.map((value,axis)=>value-originalInput.originalPreviewPorts[0][axis]))>=.01){const connection=connections.at(-1);connection.kind='original-planner-ports';connection.alignAxes=false;connection.referenceOnly=true;connection.source='original-planner-independent-frame-ports';connection.plannedPorts=structuredClone(originalInput.originalPreviewPorts);}}
      parent=instanceId;parentCoordinate=position.outletCoordinateName;previousOriginalPort=originalInput?.originalPreviewPorts?.[1]??null;
      const values=inputs.values,leftRight=scenario==='side-to-side'&&values['General.CabinetTopOpeningMode']==='LeftRight',span=leftRight?model.height:model.width,useLBracket=model.kind==='FaceToFaceLargeOffsetCorner';
      for(const [index,positionMm]of model.insulatorPositions.entries()){
        const input=frameInsulatorInput(scenario,values,model,index),design={modelId:instanceId+':insulator:'+(index+1),...await buildUniversalInsulatorDesign(input,read)};
        models.set(design.modelId,design);instances.push({instanceId:design.modelId,modelId:design.modelId,templatePath:design.templatePath,configuration:design.configuration,scope:'frame-insulator-group'});
        await connect(instanceId,frameInsulatorCoordinate(input,index),design.modelId,'安装坐标系','original-frame-insulator-route');frameModels.push({hostInstanceId:instanceId,positionMm,positionDimension:'D'+(index+1)+(input.layoutMode&&!input.isFloorStanding?'@绝缘子吊装控制草图':'@草图3'),bracketSpanSource:leftRight?'FrameHeight':'FrameWidth',design});
      }
    }
  }
  return {version:'original-structure-installation/1',instances,connections,frameInsulators:frameModels,diagnostics,source:'migrated-original-assembler-coordinate-routes',nativeModelValidationRequired:false};
}
