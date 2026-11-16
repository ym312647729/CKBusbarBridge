import {DefinitionError} from './expressions.mjs';
import {solveLinearSystem} from './linear-sketch-drive.mjs';
const dot=(left,right)=>left.reduce((sum,value,index)=>sum+value*right[index],0),subtract=(left,right)=>left.map((value,index)=>value-right[index]);
const normalized=vector=>{const magnitude=Math.hypot(...vector);if(magnitude<1e-9)throw new DefinitionError('原始配合方向无效');return vector.map(value=>value/magnitude);};

// EntityParams are already expressed in this assembly's coordinates (SW API contract).
// Preserve captured rotations; solve only a translation update whose source normals remain valid.
export async function solveAssemblyTranslations(configuration,geometryDisplacement=()=>[0,0,0],{dimensionValues={},centeredWidthMates=[]}={}){
  const components=configuration.components.filter(row=>row.suppression!==0),variables=new Map(),rows=[],evidence=[];
  for(const component of components){if(typeof component.fixedPosition!=='boolean')throw new DefinitionError('组件原生固定状态缺失：'+component.name);if(!component.fixedPosition)variables.set(component.id,variables.size*3);}
  const variableCount=variables.size*3,usedDimensions=new Set(),widthNames=new Set(centeredWidthMates);
  if(Object.values(dimensionValues).some(value=>!Number.isFinite(value)||value<0))throw new DefinitionError('装配距离必须为有限非负数');
  const componentFor=entity=>{
    if(entity.componentId===-1)return null;
    const first=entity.component?.split('/')[0],matches=components.filter(row=>row.name.split('/').at(-1)===first);
    if(matches.length!==1)throw new DefinitionError('配合组件实例无法唯一对应：'+entity.component);
    if(!entity.component.includes('/')&&entity.componentId!==matches[0].id)throw new DefinitionError('配合组件原生ID与实例不符：'+entity.component);
    return matches[0];
  };
  const add=(first,second,direction,value)=>{const row=Array(variableCount+1).fill(0);for(const [component,sign]of [[first,-1],[second,1]])if(component&&variables.has(component.id))for(let axis=0;axis<3;axis++)row[variables.get(component.id)+axis]+=sign*direction[axis];row[variableCount]=value;rows.push(row);};
  const addWeighted=(selected,weights,direction,value)=>{const row=Array(variableCount+1).fill(0);for(const [index,component]of selected.entries())if(component&&variables.has(component.id))for(let axis=0;axis<3;axis++)row[variables.get(component.id)+axis]+=weights[index]*direction[axis];row[variableCount]=value;rows.push(row);};
  for(const feature of configuration.features.filter(row=>!row.suppressed&&row.mateDefinition)){
    const mate=feature.mateDefinition;
    if([8,11].includes(mate.type)){
      const count=mate.type===8?3:4,entities=mate.entities;
      if(entities?.length!==count||entities.some(entity=>entity.referenceType!==3||!entity.reference||!Array.isArray(entity.parameters)||entity.parameters.length<6||!entity.parameters.slice(0,6).every(Number.isFinite)))throw new DefinitionError('对称或宽度配合缺少原始平面引用：'+feature.name);
      if(mate.type===11&&!widthNames.has(feature.name))throw new DefinitionError('宽度配合的居中模式未经确认：'+feature.name);
      const direction=normalized(entities[0].parameters.slice(3,6));if(entities.some(entity=>Math.abs(Math.abs(dot(direction,normalized(entity.parameters.slice(3,6))))-1)>1e-6))throw new DefinitionError('对称或宽度配合的原平面不平行：'+feature.name);
      const weights=mate.type===8?[.5,.5,-1]:[.5,.5,-.5,-.5],baseline=entities.reduce((sum,entity,index)=>sum+weights[index]*dot(direction,entity.parameters.slice(0,3))*1000,0);
      if(Math.abs(baseline)>1e-4)throw new DefinitionError('原配合不是已确认的居中位置：'+feature.name);
      const displacements=await Promise.all(entities.map(entity=>entity.componentId===-1?[0,0,0]:geometryDisplacement(entity)));
      if(displacements.some(vector=>!Array.isArray(vector)||vector.length!==3||!vector.every(Number.isFinite)))throw new DefinitionError('居中配合的受驱实体位移无效');
      const changed=displacements.reduce((sum,vector,index)=>sum+weights[index]*dot(direction,vector),0);addWeighted(entities.map(componentFor),weights,direction,-changed);evidence.push({feature:feature.name,type:mate.type,equations:1,mode:'centered',source:'native-symmetry-or-user-confirmed-original-width-center'});continue;
    }
    if(mate.entities?.length!==2||![0,1,3,5].includes(mate.type))throw new DefinitionError('装配平移求解尚未支持该原始配合：'+feature.name);
    const [first,second]=mate.entities,parameters=[first,second].map(entity=>entity.parameters);
    if(parameters.some(values=>!Array.isArray(values)||values.length<6||!values.slice(0,6).every(Number.isFinite)))throw new DefinitionError('配合缺少原始装配坐标参数：'+feature.name);
    const [firstComponent,secondComponent]=[first,second].map(componentFor),[firstNormal,secondNormal]=parameters.map(values=>normalized(values.slice(3,6))),parallel=Math.abs(dot(firstNormal,secondNormal));
    if([0,5].includes(mate.type)&&[first,second].some(entity=>entity.referenceType===1)){
      const entities=[first,second],planeIndex=entities.findIndex(entity=>entity.referenceType===3),edgeIndex=entities.findIndex(entity=>entity.referenceType===1),edge=entities[edgeIndex];
      if(planeIndex<0||edgeIndex<0||!edge.reference?.lineParameters||!edge.reference.start||!edge.reference.end||Math.abs(dot([firstNormal,secondNormal][planeIndex],[firstNormal,secondNormal][edgeIndex]))>1e-6)throw new DefinitionError('重合配合没有可验证的原平面与直边：'+feature.name);
      const direction=[firstNormal,secondNormal][planeIndex],baseline=subtract(parameters[1].slice(0,3),parameters[0].slice(0,3)).map(value=>value*1000);
      const baselineDistance=dot(direction,baseline),dimensions=[...new Set(feature.dimensions.filter(row=>row.dimensionType===0).map(row=>row.value*1000))];
      if(mate.type===0&&Math.abs(baselineDistance)>1e-4||mate.type===5&&(dimensions.length!==1||Math.abs(Math.abs(baselineDistance)-dimensions[0])>1e-4))throw new DefinitionError('源直边与原配合平面的距离不一致：'+feature.name);
      let distanceChange=0;
      if(mate.type===5){const names=[...new Set(feature.dimensions.filter(row=>row.dimensionType===0).map(row=>row.name.split('@').slice(0,-1).join('@')))];const selected=names.filter(name=>Object.hasOwn(dimensionValues,name));if(selected.length>1)throw new DefinitionError('同一原直边距离配合有多个尺寸驱动');if(selected.length){const name=selected[0];if(Math.abs(baselineDistance)<1e-8&&dimensionValues[name]!==0)throw new DefinitionError('零距离原直边配合缺少距离方向');distanceChange=Math.sign(baselineDistance)*(dimensionValues[name]-dimensions[0]);usedDimensions.add(name);}}
      const deltas=await Promise.all(entities.map(entity=>entity.componentId===-1?[0,0,0]:geometryDisplacement(entity)));
      if(deltas.some(delta=>!Array.isArray(delta)||delta.length!==3||!delta.every(Number.isFinite)))throw new DefinitionError('受驱直边位移无效');
      add(firstComponent,secondComponent,direction,distanceChange-dot(direction,subtract(deltas[1],deltas[0])));evidence.push({feature:feature.name,type:mate.type,equations:1,source:mate.type===0?'native-straight-edge-in-plane':'native-straight-edge-to-plane-distance',sourceSignedDistance:baselineDistance,drivenSignedDistance:baselineDistance+distanceChange});continue;
    }
    if(Math.abs(parallel-1)>1e-6)throw new DefinitionError('原始配合不能保持当前安装旋转：'+feature.name);
    if(mate.type===3){evidence.push({feature:feature.name,type:mate.type,rotationPreserved:true,equations:0});continue;}
    const [firstPoint,secondPoint]=parameters.map(values=>values.slice(0,3).map(value=>value*1000)),baseline=subtract(secondPoint,firstPoint),deltas=await Promise.all([first,second].map(entity=>entity.componentId===-1?[0,0,0]:geometryDisplacement(entity)));
    if(deltas.some(vector=>!Array.isArray(vector)||vector.length!==3||!vector.every(Number.isFinite)))throw new DefinitionError('受驱配合实体没有可验证的位移：'+feature.name);
    const changed=subtract(deltas[1],deltas[0]);
    if(mate.type===1){
      if([first,second].some(entity=>![2,4].includes(entity.referenceType)))throw new DefinitionError('同心配合没有原始直线或圆柱轴：'+feature.name);
      for(let axis=0;axis<3;axis++){const direction=[0,1,2].map(index=>(axis===index?1:0)-firstNormal[axis]*firstNormal[index]);if(Math.abs(dot(direction,baseline))>1e-4)throw new DefinitionError('源同心轴位置不满足配合：'+feature.name);add(firstComponent,secondComponent,direction,-dot(direction,changed));}
      evidence.push({feature:feature.name,type:mate.type,equations:3});
    }else{
      if([first,second].some(entity=>entity.referenceType!==3))throw new DefinitionError('重合或距离配合没有原始平面：'+feature.name);
      const baselineDistance=dot(firstNormal,baseline),dimensions=[...new Set(feature.dimensions.filter(row=>row.dimensionType===0).map(row=>row.value*1000))];
      if(mate.type===0&&Math.abs(baselineDistance)>1e-4||mate.type===5&&(dimensions.length!==1||Math.abs(Math.abs(baselineDistance)-dimensions[0])>1e-4))throw new DefinitionError('源平面配合位置或距离不能核验：'+feature.name);
      let distanceChange=0;
      if(mate.type===5){const names=[...new Set(feature.dimensions.filter(row=>row.dimensionType===0).map(row=>row.name.split('@').slice(0,-1).join('@')))];const selected=names.filter(name=>Object.hasOwn(dimensionValues,name));if(selected.length>1)throw new DefinitionError('同一原配合有多个尺寸驱动');if(selected.length){const name=selected[0];if(Math.abs(baselineDistance)<1e-8&&dimensionValues[name]!==0)throw new DefinitionError('零距离原配合没有确定的距离方向');distanceChange=Math.sign(baselineDistance)*(dimensionValues[name]-dimensions[0]);usedDimensions.add(name);}}
      add(firstComponent,secondComponent,firstNormal,distanceChange-dot(firstNormal,changed));evidence.push({feature:feature.name,type:mate.type,equations:1,sourceSignedDistance:baselineDistance,drivenSignedDistance:baselineDistance+distanceChange});
    }
  }
  if(Object.keys(dimensionValues).some(name=>!usedDimensions.has(name)))throw new DefinitionError('装配尺寸未对应本次有效原距离配合');
  const solution=solveLinearSystem(rows,variableCount),translations=components.map(component=>({componentId:component.id,componentName:component.name,delta:variables.has(component.id)?solution.slice(variables.get(component.id),variables.get(component.id)+3):[0,0,0],fixed:component.fixedPosition}));
  for(const row of rows)if(Math.abs(row.slice(0,variableCount).reduce((sum,value,index)=>sum+value*solution[index],0)-row[variableCount])>1e-4)throw new DefinitionError('装配平移求解未通过全部原始配合');
  return {translations,evidence,rotationChanged:false,source:'captured-native-mates-translation-system'};
}
