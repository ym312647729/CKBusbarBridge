import {DefinitionError} from './expressions.mjs';
import {solveStructureEquations} from './structure-equations.mjs';
import {baseTemplatePaths,frameTemplatePaths} from './structure-sources.mjs';
import {driveStraightWeldDefinition,weldReferenceDisplacement} from './driven-weld.mjs';
import {driveSheetDefinition} from './driven-sheet.mjs';
import {solveAssemblyTranslations} from './assembly-translations.mjs';
import {activeTemplateGeometry,swMatrix,inverseRigidMatrix,multiplyMatrix,transformPoint} from './geometry.mjs';
import {transformDirection} from './template-planes.mjs';
import {driveStructureCoordinates} from './structure-coordinates.mjs';
import {prepareStructureBusinessDrives} from './structure-business-drives.mjs';
const identity=()=>[1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1];
const canonical=name=>name.split('@').slice(0,-1).join('@');
const key=(path,configuration)=>JSON.stringify([path,configuration]);
const subtract=(first,second)=>first.map((value,axis)=>value-second[axis]);
const supportedRoots=new Set([...Object.values(baseTemplatePaths),...Object.values(frameTemplatePaths)]);

// Shared by bases and frames. Leaf geometry is driven before each parent mate
// system. Instance paths, original rotations and native fixed parts are retained.
export async function deriveStructureAssembly(source,configurationName,globals,readSource,{rootDimensions={},businessDrive=null}={}){
  if(!supportedRoots.has(source.source.path))throw new DefinitionError('结构驱动入口未对应原基座或基架');
  const business=await prepareStructureBusinessDrives(source,configurationName,globals,readSource,businessDrive);
  source=business.template;readSource=business.readTemplate;
  const originals=new Map([[source.source.path,source]]),definitions=new Map(),infos=new Map(),evidence=[],diagnostics=[];
  const read=async path=>{if(!originals.has(path))originals.set(path,await readSource(path));return originals.get(path);};
  const graph=await solveStructureEquations(source,configurationName,globals,read,{rootDimensions});
  const models=new Map(graph.models.map(model=>[key(model.templatePath,model.configuration),model]));
  function storeDefinition(template,name){
    const existing=definitions.get(template.source.path);
    if(!existing){definitions.set(template.source.path,template);return;}
    const combined=structuredClone(existing),index=combined.configurations.findIndex(row=>row.name===name);
    combined.configurations[index]=template.configurations.find(row=>row.name===name);definitions.set(template.source.path,combined);
  }
  async function visit(template,name,ancestors=[]){
    const documentKey=key(template.source.path,name);if(ancestors.includes(documentKey))throw new DefinitionError('结构装配引用循环');if(infos.has(documentKey))return infos.get(documentKey);
    const configuration=template.configurations.find(row=>row.name===name),model=models.get(documentKey);
    if(!configuration)throw new DefinitionError('结构原配置不存在');
    const dimensions=model?.dimensions??[],changedDimensions=dimensions.filter(row=>Math.abs((row.valueMm??row.value)-(row.sourceValueMm??row.sourceValue))>1e-7),stateChanged=model?.featureStates.some(state=>configuration.features.find(row=>row.name===state.feature)?.suppressed!==state.suppressed),children=new Map();
    let derived=template,referenceDisplacement=null,changed=false;
    if(configuration.components.length){
      for(const component of configuration.components.filter(row=>row.suppression!==0)){
        if(component.representation?.mode==='deferred'){diagnostics.push({path:template.source.path,component:component.name,message:'原暂缓组件不进入结构求解'});continue;}
        if(component.isVirtual)throw new DefinitionError('基座／基架包含未定义的虚拟结构件');
        const child=await read(component.path);if(child.representation?.mode==='deferred')continue;
        children.set(component.id,await visit(child,component.configuration,[...ancestors,documentKey]));
      }
      changed=configuration.businessComponentsChanged===true||children.size>0&&[...children.values()].some(child=>child.changed)||changedDimensions.length>0||stateChanged;
      if(changed){
        async function displacement(entity){
          if(entity.componentId===-1)return [0,0,0];
          const nativeNames=entity.component.split('/');let originalConfiguration=configuration,updatedConfiguration=configuration,oldPlacement=identity(),newPlacement=identity(),info;
          for(const [index,nativeName]of nativeNames.entries()){
            const matches=originalConfiguration.components.filter(row=>row.suppression!==0&&row.name.split('/').at(-1)===nativeName);
            if(matches.length!==1)throw new DefinitionError('结构配合嵌套实例不唯一：'+entity.component);
            const component=matches[0],updatedComponent=updatedConfiguration.components.find(row=>row.id===component.id);
            if(!updatedComponent)throw new DefinitionError('结构实例身份改变');
            oldPlacement=multiplyMatrix(oldPlacement,swMatrix(component.transform));newPlacement=multiplyMatrix(newPlacement,swMatrix(index===0?component.transform:updatedComponent.transform));
            const childTemplate=await read(component.path);info=infos.get(key(component.path,component.configuration));if(!info)throw new DefinitionError('结构配合引用了暂缓或未求解的组件');
            originalConfiguration=childTemplate.configurations.find(row=>row.name===component.configuration);updatedConfiguration=info.template.configurations.find(row=>row.name===component.configuration);
            if(index===nativeNames.length-1&&component.id!==entity.componentId)throw new DefinitionError('结构配合叶实例ID不一致');
          }
          const oldPoint=entity.parameters.slice(0,3).map(value=>value*1000),inverse=inverseRigidMatrix(oldPlacement),localPoint=transformPoint(oldPoint,inverse),localDirection=transformDirection(entity.parameters.slice(3,6),inverse);
          const localEntity={...entity,parameters:[...localPoint.map(value=>value/1000),...localDirection,...entity.parameters.slice(6)]};
          let delta;try{delta=info.referenceDisplacement?info.referenceDisplacement(localEntity,localPoint,localDirection):[0,0,0];}catch(error){error.source={...error.source,path:info.template.source.path,component:entity.component,localPoint,localDirection,referenceType:entity.referenceType};throw error;}
          const newPoint=transformPoint(localPoint.map((value,axis)=>value+delta[axis]),newPlacement);
          return subtract(newPoint,oldPoint);
        }
        const active=configuration.features.filter(feature=>!feature.suppressed&&feature.mateDefinition),valid=active.filter(feature=>feature.mateDefinition.entities.every(entity=>entity.reference&&(entity.componentId===-1||configuration.components.some(component=>children.has(component.id)&&component.name.split('/').at(-1)===entity.component.split('/')[0]))));
        for(const feature of active.filter(feature=>!valid.includes(feature)))diagnostics.push({path:template.source.path,feature:feature.name,message:'原配合缺少引用，未用于新定位'});
        const dimensionValues=Object.fromEntries(dimensions.filter(row=>valid.some(feature=>feature.dimensions.some(dimension=>canonical(dimension.name)===row.dimension))).map(row=>[row.dimension,row.valueMm??row.value]));
        let solution;
        try{
          solution=await solveAssemblyTranslations({...configuration,components:configuration.components.filter(row=>children.has(row.id)),features:valid},displacement,{dimensionValues,centeredWidthMates:valid.filter(feature=>feature.mateDefinition.type===11).map(feature=>feature.name)});
        }catch(error){
          if(!(error instanceof DefinitionError))throw error;
          const missing=active.filter(feature=>!valid.includes(feature)).map(feature=>feature.name),
            contextual=new DefinitionError(template.source.path+' ['+name+']：'+error.message+(missing.length?'；未捕获配合：'+missing.join('、'):''),error.source);
          contextual.cause=error;throw contextual;
        }
        derived=structuredClone(template);const updated=derived.configurations.find(row=>row.name===name);delete updated.validation;
        for(const component of updated.components){const placement=solution.translations.find(row=>row.componentId===component.id);if(placement)for(let axis=0;axis<3;axis++)component.transform[9+axis]+=placement.delta[axis]/1000;}
        // Coordinates need their own guide-sketch solve. Returning captured
        // matrices after dimensions changed would make downstream routes stale.
        if(!driveStructureCoordinates(configuration,updated,model)){
          for(const coordinate of updated.features.filter(feature=>feature.type==='CoordSys'))diagnostics.push({path:template.source.path,coordinate:coordinate.name,message:'受驱坐标系草图待求解，未返回旧安装矩阵'});
          updated.features=updated.features.filter(feature=>feature.type!=='CoordSys');
        }
        updated.equations=[];updated.offlineAssemblySolution=solution;
        for(const feature of updated.features)for(const dimension of feature.dimensions??[]){const driven=dimensions.find(row=>row.dimension===canonical(dimension.name));if(driven)dimension.value=(driven.valueMm??driven.value)/(dimension.dimensionType===2?1:1000);}
        evidence.push({path:template.source.path,configuration:name,kind:'native-structure-mates',solution});
        referenceDisplacement=entity=>{
          const datum=configuration.features.find(feature=>feature.name===entity.reference?.feature&&feature.type==='RefPlane');
          if(datum?.definition?.properties.Type===10&&!datum.dimensions.length)return [0,0,0];
          throw new DefinitionError('结构子装配体的受驱根引用尚未定义');
        };
      }
    }else if(changedDimensions.length||stateChanged){
      if(!model)throw new DefinitionError('结构叶件缺少完整方程模型');
      const geometry=activeTemplateGeometry(configuration);let drive;
      if(geometry[0]?.type==='WeldMemberFeat'){
        const paths=configuration.features.filter(feature=>feature.sketch&&geometry[0].parents.includes(feature.name)),primary=dimensions.find(row=>paths.some(path=>row.dimension.split('@')[1]===path.name));
        if(!primary)throw new DefinitionError('结构焊件缺少原路径尺寸');
        drive=driveStraightWeldDefinition(template,name,{source:'captured-governing-assembly-equation',templatePath:template.source.path,configuration:name,dimension:primary.dimension,sourceValue:primary.sourceValueMm,value:primary.valueMm},{documentModel:model});
        referenceDisplacement=(entity,point,direction)=>weldReferenceDisplacement(drive.motion,point,direction,entity.referenceType===2&&entity.reference?.circleParameters?4:entity.referenceType,entity.parameters[6]*1000);
      }else{
        drive=driveSheetDefinition(template,model);referenceDisplacement=entity=>drive.referenceDisplacement(entity);
      }
      derived=drive.template;changed=true;evidence.push({path:template.source.path,configuration:name,kind:'native-structure-leaf',evidence:drive.evidence});
    }
    const info={template:derived,changed,referenceDisplacement};infos.set(documentKey,info);storeDefinition(derived,name);return info;
  }
  const root=await visit(source,configurationName);
  const rootModel=models.get(key(source.source.path,configurationName)),parameters=[];
  if(Object.values(frameTemplatePaths).includes(source.source.path)){const globals=rootModel.globalVariables,width=globals['基架宽度'],length=globals['基架长度'],height=globals['基架高度'],side=source.source.path===frameTemplatePaths.SideToSidePhaseChange029,corner=source.source.path===frameTemplatePaths.FaceToFaceLargeOffsetCorner028;
    for(const [name,label,value,edge]of [['width','宽度',width,side?[[0,0,0],[0,width,0]]:[[0,corner?0:height/2,-width/2],[0,corner?0:height/2,width/2]]],['length','长度',length,[[-length/2,side?width/2:corner?0:height/2,0],[length/2,side?width/2:corner?0:height/2,0]]],['height','高度',height,side?[[0,width/2,-height/2],[0,width/2,height/2]]:corner?[[0,-height/2,0],[0,height/2,0]]:[[0,0,0],[0,height,0]]]])parameters.push({name:'frame.'+name,label,value,editable:true,annotationEdges:[edge],source:'original-structure-global-dimension-control-planes'});}
  return {template:root.template,loadTemplate:async path=>definitions.get(path)??read(path),graph,evidence:[...business.evidence,...evidence],diagnostics,parameters,source:'original-base-frame-geometry-and-mate-system',nativeModelValidationRequired:false};
}
