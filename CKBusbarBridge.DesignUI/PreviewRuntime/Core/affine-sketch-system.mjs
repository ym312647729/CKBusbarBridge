import {DefinitionError} from './expressions.mjs';

// A construction endpoint may be free while every physical profile coordinate
// is unique. Prove that distinction from the native system's nullspace before
// retaining captured values for free construction coordinates.
export function solveAffineSketchSystem(equations,variableCount,freeValues,protectedForms=[]){
  const rows=equations.map(row=>[...row]),pivots=[];let pivotRow=0;
  for(let column=0;column<variableCount&&pivotRow<rows.length;column++){
    let selected=pivotRow;for(let index=pivotRow+1;index<rows.length;index++)if(Math.abs(rows[index][column])>Math.abs(rows[selected][column]))selected=index;
    if(Math.abs(rows[selected][column])<1e-9)continue;
    [rows[selected],rows[pivotRow]]=[rows[pivotRow],rows[selected]];
    const divisor=rows[pivotRow][column];rows[pivotRow]=rows[pivotRow].map(value=>value/divisor);
    for(let index=0;index<rows.length;index++)if(index!==pivotRow){const factor=rows[index][column];if(Math.abs(factor)>1e-12)for(let offset=column;offset<=variableCount;offset++)rows[index][offset]-=factor*rows[pivotRow][offset];}
    pivots.push(column);pivotRow++;
  }
  for(const row of rows)if(row.slice(0,variableCount).every(value=>Math.abs(value)<1e-8)&&Math.abs(row[variableCount])>1e-5)throw new DefinitionError('草图线性约束冲突');
  const freeColumns=Array.from({length:variableCount},(_,index)=>index).filter(index=>!pivots.includes(index));
  for(const column of freeColumns){const direction=Array(variableCount).fill(0);direction[column]=1;for(let row=0;row<pivots.length;row++)direction[pivots[row]]=-rows[row][column];for(const form of protectedForms)if(Math.abs(form.terms.reduce((sum,[index,coefficient])=>sum+coefficient*direction[index],0))>1e-8)throw new DefinitionError(form.message??'原草图实体仍有未确定的自由度');}
  const values=Array(variableCount).fill(0);for(const column of freeColumns)values[column]=freeValues[column];
  for(let row=0;row<pivots.length;row++)values[pivots[row]]=rows[row][variableCount]-freeColumns.reduce((sum,column)=>sum+rows[row][column]*values[column],0);
  return {values,freeConstructionCoordinates:freeColumns.length};
}
