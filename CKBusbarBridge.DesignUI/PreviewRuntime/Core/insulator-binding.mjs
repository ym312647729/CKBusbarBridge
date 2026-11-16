import {DefinitionError} from './expressions.mjs';
import {universalInsulatorTemplatePath} from './insulator-design.mjs';

export function createInsulatorInstance(draft,template,existing){
  if(template.source?.path!==universalInsulatorTemplatePath||!template.configurations.some(configuration=>configuration.name==='默认'))throw new DefinitionError('绝缘子预览未对应原通用模板');
  return {id:existing?.id??crypto.randomUUID(),name:existing?.name??draft.name,kind:'insulator-group',category:'insulator',parentId:null,templatePath:universalInsulatorTemplatePath,configuration:'默认',sourceSha256:template.source.sha256,parameters:{},suppression:{},transform:existing?.transform??[1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1],visible:existing?.visible??true,insulatorBinding:{planId:draft.id},insulatorPlanInput:structuredClone(draft.input)};
}

export function resolveInsulatorInstances(objects,plans){
  const used=new Set();return objects.map(object=>{
    if(!object.insulatorBinding){if(object.kind==='insulator-group'||object.insulatorPlanInput&&!object.structureSupportBinding)throw new DefinitionError('绝缘子组缺少业务布局绑定');return object;}
    const plan=plans.find(plan=>plan.id===object.insulatorBinding.planId);
    if(!plan||Object.keys(object.insulatorBinding).length!==1||object.kind!=='insulator-group'||object.category!=='insulator'||object.parentId||object.templatePath!==universalInsulatorTemplatePath||object.configuration!=='默认'||typeof object.sourceSha256!=='string'||Object.keys(object.parameters??{}).length||Object.keys(object.suppression??{}).length)throw new DefinitionError('绝缘子组的原模板或业务布局身份不符');
    if(used.has(plan.id))throw new DefinitionError('同一绝缘子布局不能重复安装预览组');used.add(plan.id);
    return {...object,insulatorPlanInput:structuredClone(plan.input)};
  });
}
