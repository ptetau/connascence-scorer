class AddStatus < ActiveRecord::Migration[7.1]
  def change
    add_column :orders, :status, :integer, default: 3
    add_index :orders, :status
    change_column_null :orders, :status, false
    add_column :orders, :note, :string
    add_index :orders, :note
    change_column_null :orders, :note, true
  end
end
