import {DefinitionError} from './expressions.mjs';
import {planCabinetBases} from './cabinet-base-planning.mjs';
import {baseTemplatePaths} from './structure-sources.mjs';
import sources from '../catalog/structure-scene-sources.json' with {type:'json'};

// Cabinet bases and frame instances are products of the two design modules.
// Their existence never depends on an extra per-part preview button.
export function resolveDesignModuleObjects(project,frameDesign){
  if(!project.designModules?.cabinet)return project.objects;
  const planned=planCabinetBases(project.scenario,project.cabinetInputs[project.scenario]);
  const objects=structuredClone(project.objects).filter(object=>!object.baseBinding||object.baseBinding.scenario===project.scenario);
  const create=(key,path,category,name)=>{
    const source=sources.models.find(row=>row.path===path);if(!source)throw new DefinitionError('设计模块缺少原模型身份：'+path);
    const id=project.id+':'+key;if(objects.some(object=>object.id===id))throw new DefinitionError('自动设计对象标识已被占用');
    return {id,name,category,parentId:null,templatePath:source.path,sourceSha256:source.sha256,configuration:source.configuration,parameters:{},suppression:{},transform:[1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1],visible:true};
  };
  for(const position of planned.plans)for(const role of ['base',...position.elevatedBaseModel?['elevated']:[]]){
    if(objects.some(object=>object.baseBinding?.scenario===project.scenario&&object.baseBinding.positionName===position.positionName&&object.baseBinding.role===role))continue;
    const model=role==='base'?position.baseModel:position.elevatedBaseModel,object=create('base:'+position.positionName+':'+role,baseTemplatePaths[model.kind],role==='base'?'base':'raisedBase',position.positionName+(role==='base'?'基座':'增高基座'));
    object.baseBinding={version:'original-base-preview/1',scenario:project.scenario,positionName:position.positionName,role};objects.push(object);
  }
  if(project.designModules.bridge){
    const retained=objects.filter(object=>!object.frameBinding||frameDesign?.plans.some(plan=>plan.positionKey===object.frameBinding.positionKey&&plan.model.templatePath===object.templatePath));objects.splice(0,objects.length,...retained);
    for(const position of frameDesign?.plans??[]){if(objects.some(object=>object.frameBinding?.positionKey===position.positionKey))continue;const object=create('frame:'+position.positionKey,position.model.templatePath,'frame',position.positionKey+' · '+position.model.sourceName);object.frameBinding={version:'original-frame-preview/1',positionKey:position.positionKey};objects.push(object);}
  }
  return objects;
}
