import {DefinitionError} from './expressions.mjs';
import {planCabinetBases} from './cabinet-base-planning.mjs';
import {baseTemplatePaths} from './structure-sources.mjs';
import {baseInstallationDimensions} from './structure-installation.mjs';
import sources from '../catalog/structure-scene-sources.json' with {type:'json'};
import {swMatrix} from './geometry.mjs';

export function basePreviewFields(scenario,record,position,role){
  const cabinet=scenario==='face-to-wall'?'Cabinet':position.positionName.startsWith('A')?'ACabinet':'BCabinet',hole=cabinet+'.'+(position.positionName.endsWith('前')?'FrontHole':'BackHole'),side=scenario==='side-to-side',model=role==='elevated'?position.elevatedBaseModel:position.baseModel,kind=model.kind,prefix=hole+(side?kind==='Base1'?'.Base1':'.Base2':''),fields=[];
  const add=(name,label,axis,extent,below=0)=>fields.push({name,label,axis,extent,below,value:record.values[name]});
  const stackedHeight=role==='base'?position.elevatedBaseModel?.globalVariables['高度']??0:0;
  add(role==='elevated'?hole+(side?'.Base2.ElevatedBaseHeight':'.ExtensionBaseHeight'):prefix+'.BaseHeight',
    stackedHeight?'基座总高':'基座高度',role==='elevated'?0:1,model.globalVariables[kind==='ElevatedBase'?'高度':'基座高'],stackedHeight);
  if(kind==='Base2')add(prefix+'.BaseWidth','基座宽度',2,model.globalVariables['基座宽']);
  else if(kind!=='Base1')add(cabinet+'.CabinetWidth','柜宽 / 基座宽度',2,model.globalVariables[kind==='ElevatedBase'?'宽度':'基座宽']);
  add(hole+'.FixedHoleSpacing','固定孔间距',role==='elevated'?1:0,record.values[hole+'.FixedHoleSpacing']);
  return fields;
}

export function resolveBasePreviewInstances(objects,inputs){
  const plans=new Map();
  return objects.flatMap(object=>{
    if(!object.baseBinding)return [object];
    const binding=object.baseBinding,source=sources.models.find(source=>source.path===object.templatePath);
    if(binding.version!=='original-base-preview/1'||Object.keys(binding).some(name=>!['version','scenario','positionName','role'].includes(name))||!['base','elevated'].includes(binding.role)||!inputs[binding.scenario]||!source||object.sourceSha256!==source.sha256||object.configuration!==source.configuration||object.category!==(binding.role==='elevated'?'raisedBase':'base')||object.parentId||object.kind||object.frameBinding||object.cabinetBinding||object.insulatorBinding||object.hangerBinding||Object.keys(object.parameters??{}).length||Object.keys(object.suppression??{}).length)throw new DefinitionError('基座预览未对应当前原业务组');
    if(!plans.has(binding.scenario))plans.set(binding.scenario,planCabinetBases(binding.scenario,inputs[binding.scenario]));
    const plan=plans.get(binding.scenario),position=plan.plans.find(position=>position.positionName===binding.positionName),model=binding.role==='base'?position?.baseModel:position?.elevatedBaseModel;
    if(!model)return [];
    const target=sources.models.find(source=>source.path===baseTemplatePaths[model.kind]);if(!target)throw new DefinitionError('基座缺少已存来源身份');
    const placements=(position.insulators??[position.insulator]).filter(placement=>(placement.hostModelKind??placement.targetBaseKind)===model.kind);
    return [{...object,templatePath:target.path,sourceSha256:target.sha256,configuration:target.configuration,structurePlanInput:{globalVariables:structuredClone(model.globalVariables),rootDimensions:baseInstallationDimensions({insulators:placements}),businessDrive:model.kind==='Base'?{kind:'Base',hasElevatedBase:position.hasElevatedBase}:null,kind:model.kind,positionName:position.positionName,installationCoordinateName:binding.role==='elevated'?'下':position.baseInstallationCoordinateName??'安装坐标系',fields:basePreviewFields(binding.scenario,plan.record,position,binding.role),installationStatus:'coordinate-driven-schematic-model'}}];
  });
}

export function basePreviewParameters(input,configuration){
  const elevated=input.kind==='ElevatedBase',globals=input.globalVariables,
    size=elevated?[globals['高度'],globals['深度'],globals['宽度']]:[globals['基座深'],globals['基座高'],globals['基座宽']],
    datumName=input.installationCoordinateName;
  const coordinate=name=>{
    const features=configuration.features.filter(feature=>!feature.suppressed&&feature.type==='CoordSys'&&feature.name===name),
      transform=features[0]?.definition?.properties?.Transform?.transform;
    if(features.length!==1||!Array.isArray(transform)||transform.length!==16||!transform.every(Number.isFinite))
      throw new DefinitionError(input.positionName+' '+input.kind+'：基座标注缺少原安装坐标 '+name);
    return swMatrix(transform).slice(12,15);
  };
  if(size.some(value=>!Number.isFinite(value)||value<=0))throw new DefinitionError(input.positionName+' '+input.kind+'：基座标注缺少驱动外形尺寸');
  const datum=coordinate(datumName),
    // Base2's mounting datum is on the rotated inlet and laterally displaced
    // along native Z. The bridge connection datum carries the native Z centre.
    center=input.kind==='Base2'?[datum[0],datum[1]+size[1]/2,coordinate('基座安装坐标系')[2]]:
      elevated?[datum[0]-size[0]/2,datum[1]+size[1]/2-21.5,datum[2]]:[datum[0]+size[0]/2-21.5,datum[1]+size[1]/2,datum[2]],
    low=center.map((value,axis)=>value-size[axis]/2),high=center.map((value,axis)=>value+size[axis]/2);
  // Dimension lines follow three distinct outside edges. Origins are verified native
  // installation datums (including Base1/Base2's -0.75 mm transverse offset), never mesh bounds.
  return input.fields.map(field=>{
    const offset=30,a=[...low],b=[...low];
    if(field.axis===0){a[1]=b[1]=low[1]-offset;a[2]=b[2]=high[2];}
    else if(field.axis===1){a[0]=b[0]=low[0]-offset;a[2]=b[2]=low[2];}
    else {a[0]=b[0]=high[0]+offset;a[1]=b[1]=high[1];}
    a[field.axis]=center[field.axis]-field.extent/2-field.below;b[field.axis]=center[field.axis]+field.extent/2;
    return {name:field.name,label:field.label,value:field.value,editable:true,annotationEdges:[[a.map(Math.fround),b.map(Math.fround)]],source:'original-base-installation-datum-and-driven-outer-edge'};
  });
}
