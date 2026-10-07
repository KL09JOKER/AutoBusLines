import React from 'react';
import { Icon } from 'cs2/ui';
import styles from './Checkbox.module.scss';

interface CheckboxProps {
  isChecked: boolean;
  onValueToggle: (newVal: boolean) => void;
  className?: string;
}

export const Checkbox: React.FC<CheckboxProps> = ({ isChecked, onValueToggle, className }) => {
  const checkmarkSrc = 'Media/Glyphs/Checkmark.svg';
  return (
    <div
      className={`${styles.checkboxContainer} ${isChecked ? styles.checked : ''} ${className || ''}`}
      onClick={(e) => {
        e.stopPropagation();
        onValueToggle(!isChecked);
      }}
    >
      {isChecked && <Icon src={checkmarkSrc} className={styles.checkmarkIcon} tinted={true} />}
    </div>
  );
};
