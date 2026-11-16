import {DefinitionError} from './expressions.mjs';
import {frameInstallationDimensions} from './structure-installation.mjs';
export function resolveFramePreviewInstances(objects,frameDesign){
  return objects.map(object=>{
    if(!object.frameBinding){if(object.structurePlanInput&&!object.baseBinding)throw new DefinitionError('结构受驱参数缺少原业务组');return object;}
    const binding=object.frameBinding,position=frameDesign?.plans.find(plan=>plan.positionKey===binding.positionKey);
    if(binding.version!=='original-frame-preview/1'||Object.keys(binding).some(name=>!['version','positionKey'].includes(name))||!position||object.category!=='frame'||object.parentId||object.kind||object.hangerBinding||object.cabinetBinding||object.insulatorBinding||object.configuration!=='默认'||object.templatePath!==position.model.templatePath||Object.keys(object.parameters??{}).length||Object.keys(object.suppression??{}).length)throw new DefinitionError('基架预览未对应当前原参数组');
    return {...object,structurePlanInput:{globalVariables:structuredClone(position.model.globalVariables),rootDimensions:frameInstallationDimensions(position.model),businessDrive:position.model.smallOffset?{kind:'FaceToFaceSmallOffset032',suppressTemplateAngleNotches:position.model.suppressTemplateAngleNotches}:null,sourceName:position.model.sourceName,width:position.model.width,length:position.model.length,height:position.model.height,installationStatus:'coordinate-driven-schematic-model'}};
  });
}
